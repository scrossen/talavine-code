using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SwarmCore.Agent.BusinessDispatch;

public sealed record BusinessBrowserScope(string AccountId, string OwnerUserId, string Sender, string ReceiverId,
    string ConnectionId, Dictionary<string,string> TargetEvidence, DateTimeOffset ExpiresUtc, int DailyLimit = 3, bool Enabled = true);
public sealed record BusinessBrowserIntent(string Kind,string ConnectionId,string ExpectedAccountHandle,string TargetHandle,string EvidenceHash,Guid OperationId);
public sealed record BusinessNativeReservation(BusinessAdapterOperation Operation,DateTimeOffset ReservedUtc,bool Confirmed = false);

/// <summary>Exclusive native admission journal. A crash after reservation permits only observation, never another effect.</summary>
public sealed class BusinessNativeReceiverJournal : IDisposable
{
    private readonly string _directory;
    private readonly FileStream _lock;
    private sealed record Configuration(BusinessBrowserScope Scope,string CredentialHash);
    /// <summary>
    /// Confirmed reservations are kept for replay and receipt reads for longer than any receiver scope can stay
    /// live (90 days); uncertain ones are kept until observation confirms them.
    /// </summary>
    private static readonly TimeSpan ConfirmedRetention=TimeSpan.FromDays(91);
    private DateTime _maintainedDay;
    public BusinessNativeReceiverJournal(string directory)
    {
        _directory=Path.GetFullPath(directory);Directory.CreateDirectory(_directory);
        _lock=new FileStream(Path.Combine(_directory,"receiver.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
        Maintain(DateTimeOffset.UtcNow);
    }
    private static string HashCredential(string token)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    private string ConfigPath=>Path.Combine(_directory,"scope.json");
    public BusinessBrowserScope? Authenticate(string token)
    {
        if(token.Length is < 43 or > 200 || !File.Exists(ConfigPath))return null;
        var c=JsonSerializer.Deserialize<Configuration>(File.ReadAllText(ConfigPath));
        return c is not null&&CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(c.CredentialHash),Encoding.ASCII.GetBytes(HashCredential(token)))?c.Scope:null;
    }
    public void Configure(BusinessBrowserScope scope,string credential)
    {
        if(credential.Length is < 43 or > 200||scope.DailyLimit is < 1 or > 20||scope.ExpiresUtc<=DateTimeOffset.UtcNow||scope.ExpiresUtc>DateTimeOffset.UtcNow.AddDays(90)
            ||new[]{scope.AccountId,scope.OwnerUserId,scope.Sender,scope.ReceiverId,scope.ConnectionId}.Any(x=>string.IsNullOrWhiteSpace(x)||x.Length>200)
            ||scope.TargetEvidence.Count is < 1 or > 100||scope.TargetEvidence.Any(x=>!Marketing.SocialProspectingPlan.ValidHandle(x.Key)||x.Value.Length!=64))throw new ArgumentException("Exact current account, browser connection, target evidence, expiry, allowance and distinct protected credential are required.");
        if(File.Exists(ConfigPath))
        {
            var old=JsonSerializer.Deserialize<Configuration>(File.ReadAllText(ConfigPath))!.Scope;
            if(old.AccountId!=scope.AccountId||old.OwnerUserId!=scope.OwnerUserId||old.ConnectionId!=scope.ConnectionId||old.ReceiverId!=scope.ReceiverId||old.Sender!=scope.Sender)throw new InvalidOperationException("Pinned receiver identity cannot be replaced; configure a separate receiver directory.");
        }
        Save(ConfigPath,new Configuration(scope,HashCredential(credential)));
    }
    private string PathFor(string operation)=>Path.Combine(_directory,"operation-"+BusinessDispatchContract.Hash(operation)+".json");
    public BusinessNativeReservation? Read(string operation)=>File.Exists(PathFor(operation))?JsonSerializer.Deserialize<BusinessNativeReservation>(File.ReadAllText(PathFor(operation))):null;
    public BusinessNativeReservation Reserve(BusinessAdapterOperation operation,BusinessBrowserScope scope)
    {
        if(Read(operation.OperationId) is{} prior)return prior.Operation==operation?prior:throw new InvalidOperationException("Frozen operation changed.");
        var now=DateTimeOffset.UtcNow;
        if(now.UtcDateTime.Date!=_maintainedDay)Maintain(now);
        // Today's allowance is counted from per-day marker names, never by reading every reservation.
        var count=Directory.EnumerateFiles(_directory,DayMarkerPrefix(now.UtcDateTime.Date)+"*").Count();
        if(count>=scope.DailyLimit)throw new InvalidOperationException("Receiver daily allowance is already reserved, including uncertainty.");
        // The marker is written first: a crash in between over-counts the allowance, never under-counts it.
        File.WriteAllBytes(MarkerFor(now.UtcDateTime.Date,operation.OperationId),[]);
        var reservation=new BusinessNativeReservation(operation,now);Save(PathFor(operation.OperationId),reservation);return reservation;
    }
    private static string DayMarkerPrefix(DateTime day)=>"day-"+day.ToString("yyyyMMdd",System.Globalization.CultureInfo.InvariantCulture)+"-";
    private string MarkerFor(DateTime day,string operation)=>Path.Combine(_directory,DayMarkerPrefix(day)+BusinessDispatchContract.Hash(operation));
    /// <summary>
    /// Bounds the journal: interrupted writes are discarded, earlier days' allowance markers are removed, confirmed
    /// reservations past retention are deleted, and today's reservations written before markers existed are counted.
    /// Unreadable reservations are left in place and never block admission.
    /// </summary>
    private void Maintain(DateTimeOffset now)
    {
        var today=now.UtcDateTime.Date;var todayMarkers=DayMarkerPrefix(today);var cutoff=now-ConfirmedRetention;
        foreach(var pending in Directory.EnumerateFiles(_directory,"*.pending").ToArray())File.Delete(pending);
        foreach(var marker in Directory.EnumerateFiles(_directory,"day-*").ToArray())
            if(!Path.GetFileName(marker).StartsWith(todayMarkers,StringComparison.Ordinal))File.Delete(marker);
        foreach(var path in Directory.EnumerateFiles(_directory,"operation-*.json").ToArray())
        {
            // Only reservations written today (possibly unmarked) or before the cutoff need to be opened.
            var written=File.GetLastWriteTimeUtc(path);
            if(written.Date!=today&&written>=cutoff.UtcDateTime)continue;
            BusinessNativeReservation? reservation;
            try{reservation=JsonSerializer.Deserialize<BusinessNativeReservation>(File.ReadAllText(path));}
            catch(JsonException){continue;}
            if(reservation is null)continue;
            if(reservation.Confirmed&&reservation.ReservedUtc<cutoff)File.Delete(path);
            else if(reservation.ReservedUtc.UtcDateTime.Date==today&&!File.Exists(MarkerFor(today,reservation.Operation.OperationId)))
                File.WriteAllBytes(MarkerFor(today,reservation.Operation.OperationId),[]);
        }
        _maintainedDay=today;
    }
    public void Confirm(BusinessNativeReservation reservation)=>Save(PathFor(reservation.Operation.OperationId),reservation with{Confirmed=true});
    private static void Save<T>(string path,T value)
    {
        var temp=path+".pending";using(var stream=new FileStream(temp,FileMode.Create,FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough)){JsonSerializer.Serialize(stream,value);stream.Flush(true);}File.Move(temp,path,true);
    }
    public void Dispose()=>_lock.Dispose();
}
