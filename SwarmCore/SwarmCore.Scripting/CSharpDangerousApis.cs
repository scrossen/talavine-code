namespace SwarmCore.Scripting;

/// <summary>
/// The single source of truth for which C# APIs are dangerous when running
/// agent- or LLM-authored code. Both the lightweight regex pass and a Roslyn
/// semantic-symbol pass consume these. Apps compose their own policy from these
/// generic lists plus any app-specific additions, so a newly discovered bypass
/// is fixed once here and every Swarm app benefits.
/// </summary>
public static class CSharpDangerousApis
{
    /// <summary>
    /// Regex patterns for operations that are NEVER allowed in sandboxed scripts:
    /// process control, assembly/AppDomain loading, private reflection, runtime
    /// code emission, native
    /// interop, unsafe code, raw sockets, registry, environment mutation, and raw
    /// SQL. Matched case-insensitively.
    /// </summary>
    public static readonly string[] BlockedPatterns =
    {
        @"\bProcess\.Start\b",
        @"\bSystem\.Diagnostics\.Process\b",
        @"\bAssembly\.Load\b",
        @"\bAssembly\.LoadFile\b",
        @"\bAssembly\.LoadFrom\b",
        @"\bAssembly\.LoadModule\b",
        @"\bAssembly\s*\.\s*UnsafeLoadFrom\b",
        @"\bAssemblyLoadContext\b",
        @"\bCreateInstanceFrom\b",
        // COM activation by name reaches shell automation objects that start processes.
        @"\bGetTypeFromProgID\b",
        @"\bGetTypeFromCLSID\b",
        @"\bAppDomain\s*\.\s*(?:Load|SetData)\b",
        @"\bEnvironment\.Exit\b",
        @"\bEnvironment\.SetEnvironmentVariable\b",
        // Reading host environment/config is an exfiltration vector (e.g. tenant connection strings
        // injected as env vars on Azure App Service) — sandboxed scripts have no business reading it.
        @"\bEnvironment\.GetEnvironmentVariable",
        @"\bGetEnvironmentVariables\b",
        @"\bAppContext\b",
        // Public runtime metadata is useful agent context and is intentionally allowed.
        // The hard boundary is private state or loading/emitting new code into the host.
        @"\bBindingFlags\s*\.\s*NonPublic\b",
        @"\bBindingFlags\s*\.\s*FlattenHierarchy\b",
        @"\bSystem\.Reflection\.Emit\b",
        @"\bDynamicMethod\b",
        @"\bAssemblyBuilder\b",
        @"\bTypeBuilder\b",
        @"\bRegistry\b",
        @"\bPInvoke\b",
        @"\bDllImport\b",
        // Any extern method is native code, whatever attribute spelling declares it.
        @"\bextern\b",
        @"\bMemoryMarshal\b",
        @"\bunsafe\b",
        @"\bfixed\s*\(",
        @"\bstackalloc\b",
        @"\bGC\.Collect\b",
        @"\bThread\.Abort\b",
        @"\bWebRequest\b",
        @"\bSocket\b",
        @"\bTcpClient\b",
        @"\bUdpClient\b",
        @"\bHttpWebRequest\b",
        @"\bExecuteSqlRaw\b",
        @"\bExecuteSqlInterpolated\b",
        @"\bSqlConnection\b",
        @"\bSqlCommand\b",
    };

    /// <summary>
    /// Regex patterns for generic side effects (filesystem writes, raw networking)
    /// that an app may either require approval for or block outright depending on
    /// its threat model.
    /// </summary>
    public static readonly string[] WriteAndNetworkPatterns =
    {
        @"\bFile\.Write",
        @"\bFile\.Delete",
        @"\bFile\.Move",
        @"\bFile\.Copy",
        @"\bFile\.Create",
        @"\bFile\.AppendAll",
        @"\bFile\.Open",
        @"\bFile\.Replace\b",
        @"\bFile\.Set",
        @"\bFile\.Encrypt\b",
        @"\bFile\.Decrypt\b",
        @"\bFileStream\b",
        @"\bFileInfo\b",
        @"\bDirectoryInfo\b",
        @"\bRandomAccess\b",
        @"\bMemoryMappedFile\b",
        @"\bStreamWriter\b",
        @"\bDirectory\.Delete\b",
        @"\bDirectory\.Move\b",
        @"\bDirectory\.CreateDirectory\b",
        @"\bHttpClient\b",
        @"\bWebClient\b",
        @"\bIHttpClientFactory\b",
        // Type names are case-sensitive in C#; matching them exactly keeps locals such as
        // `zipFile` or `listener` from reading as the API. The semantic pass over the bound
        // symbols is the complete check; these are the textual backstop.
        @"(?-i:\bZipFile\s*\.\s*(?:CreateFromDirectory|ExtractToDirectory|Open)(?:Async)?\b)",
        @"(?-i:\bHttpMessageInvoker\b)",
        @"(?-i:\bSocketsHttpHandler\b)",
        @"(?-i:\bHttpClientHandler\b)",
        @"(?-i:\bClientWebSocket\b)",
        @"(?-i:\bHttpListener\b)",
        @"(?-i:\bSmtpClient\b)",
        @"(?-i:\bTcpListener\b)",
        @"(?-i:\bNamedPipe(?:Client|Server)Stream\b)",
        @"(?-i:\bDns\s*\.)",
    };

    /// <summary>
    /// Public reflection operations that can execute code or read/write values. Talavine treats
    /// these as objective-authorized work (the same tier as file/network mutations), while stricter
    /// pure-compute hosts can block the tier. Public type/member metadata inspection remains allowed.
    /// </summary>
    public static readonly string[] ReflectionInvocationPatterns =
    {
        @"\bActivator\s*\.\s*CreateInstance\b",
        @"\bMethodInfo\s*\.\s*Invoke\b",
        @"\bMethodBase\s*\.\s*Invoke\b",
        @"\bConstructorInfo\s*\.\s*Invoke\b",
        @"\bPropertyInfo\s*\.\s*(?:GetValue|SetValue)\b",
        @"\bFieldInfo\s*\.\s*(?:GetValue|SetValue)\b",
        @"\bEventInfo\s*\.\s*(?:AddEventHandler|RemoveEventHandler)\b",
        @"\bDelegate\s*\.\s*DynamicInvoke\b",
        @"\bMethodInfo\s*\.\s*CreateDelegate\b",
        @"\bDelegate\s*\.\s*CreateDelegate\b",
        @"(?-i:\b(?:MethodInvoker|ConstructorInvoker)\s*\.\s*Create\b)",
    };

    /// <summary>
    /// Runtime type lookup by name. On its own it only yields metadata, but it is the
    /// bridge from a string the analyzer cannot see (concatenated, decoded, read from
    /// data) to a <c>Type</c> that reflection invocation can then act on, which is how
    /// a text-matching scan gets bypassed. Confined hosts block it together with
    /// <see cref="ReflectionInvocationPatterns"/>; <c>typeof(T)</c>, <c>obj.GetType()</c>
    /// and <c>Assembly.GetTypes()</c> enumeration remain ordinary public metadata.
    /// </summary>
    public static readonly string[] RuntimeTypeLookupPatterns =
    {
        @"\bType\s*\.\s*GetType\s*\(",
        @"\bType\s*\.\s*ReflectionOnlyGetType\b",
    };

    /// <summary>Symbol prefixes for <see cref="RuntimeTypeLookupPatterns"/>.</summary>
    public static readonly string[] RuntimeTypeLookupSymbolPrefixes =
    {
        "System.Type.GetType",
        "System.Type.ReflectionOnlyGetType",
    };

    /// <summary>Fully-qualified symbol prefixes that are never allowed (for Roslyn semantic analysis).</summary>
    public static readonly string[] BlockedSymbolPrefixes =
    {
        "System.Diagnostics.Process",
        "System.Reflection.Assembly.Load",
        "System.Reflection.Assembly.LoadFile",
        "System.Reflection.Assembly.LoadFrom",
        "System.Reflection.Assembly.LoadModule",
        "System.Reflection.Assembly.UnsafeLoadFrom",
        "System.Runtime.Loader.AssemblyLoadContext",
        "System.Activator.CreateInstanceFrom",
        "System.AppDomain.CreateInstanceFrom",
        "System.Type.GetTypeFromProgID",
        "System.Type.GetTypeFromCLSID",
        "System.Reflection.Emit",
        "System.Reflection.BindingFlags.NonPublic",
        "System.Reflection.BindingFlags.FlattenHierarchy",
        "System.AppDomain.Load",
        "System.AppDomain.SetData",
        "System.AppContext",
        "System.Environment.Exit",
        "System.Environment.SetEnvironmentVariable",
        "System.Environment.GetEnvironmentVariable",
        "System.Environment.GetEnvironmentVariables",
        "Microsoft.Win32.Registry",
        "System.Net.Sockets.Socket",
        "System.Net.Sockets.TcpClient",
        "System.Net.Sockets.UdpClient",
        "Microsoft.Data.Sqlite.SqliteConnection",
        "Microsoft.Data.Sqlite.SqliteCommand",
        "System.Runtime.InteropServices.NativeLibrary",
        "System.Runtime.InteropServices.Marshal",
        "System.Runtime.InteropServices.DllImportAttribute",
        "System.Runtime.InteropServices.LibraryImportAttribute",
        // Unchecked memory access without the `unsafe` keyword.
        "System.Runtime.InteropServices.MemoryMarshal",
        "System.Runtime.CompilerServices.Unsafe",
    };

    /// <summary>Fully-qualified symbol prefixes for generic write/network side effects.</summary>
    public static readonly string[] WriteAndNetworkSymbolPrefixes =
    {
        "System.IO.File.Write",
        "System.IO.File.Delete",
        "System.IO.File.Move",
        "System.IO.File.Copy",
        "System.IO.File.Create",
        "System.IO.File.Append",
        "System.IO.File.Open",
        "System.IO.File.Replace",
        "System.IO.File.Set",
        "System.IO.File.Encrypt",
        "System.IO.File.Decrypt",
        "System.IO.FileStream",
        "System.IO.FileInfo",
        "System.IO.DirectoryInfo",
        "System.IO.RandomAccess",
        "System.IO.MemoryMappedFiles.MemoryMappedFile",
        "System.IO.StreamWriter",
        "System.IO.Directory.Delete",
        "System.IO.Directory.Move",
        "System.IO.Directory.CreateDirectory",
        "System.IO.Directory.CreateSymbolicLink",
        "System.IO.Directory.CreateTempSubdirectory",
        "System.IO.Directory.Set",
        "System.IO.FileSystemInfo",
        "System.IO.DriveInfo.VolumeLabel",
        "System.IO.Path.GetTempFileName",
        "System.IO.Pipes.",
        "System.IO.IsolatedStorage.",
        "System.IO.Compression.ZipFile.CreateFromDirectory",
        "System.IO.Compression.ZipFile.ExtractToDirectory",
        "System.IO.Compression.ZipFile.Open(",
        "System.IO.Compression.ZipFileExtensions",
        // Serializers given a path or URI open the file or fetch the URL themselves. The
        // (string... signature forms leave the Stream/TextWriter/StringBuilder overloads, which
        // only reach a file through an already-flagged stream, as ordinary compute.
        "System.Xml.Linq.XDocument.Save(string",
        "System.Xml.Linq.XDocument.Load(string",
        "System.Xml.Linq.XElement.Save(string",
        "System.Xml.Linq.XElement.Load(string",
        "System.Xml.Linq.XStreamingElement.Save(string",
        "System.Xml.XmlDocument.Save(string",
        "System.Xml.XmlDocument.Load(string",
        "System.Xml.XmlWriter.Create(string",
        "System.Xml.XmlReader.Create(string",
        "System.Xml.XmlTextWriter",
        "System.Xml.XmlTextReader",
        "System.Xml.XPath.XPathDocument..ctor(string",
        "System.Xml.Xsl.XslCompiledTransform",
        "System.Data.DataSet.WriteXml(string",
        "System.Data.DataSet.WriteXmlSchema(string",
        "System.Data.DataSet.ReadXml(string",
        "System.Data.DataSet.ReadXmlSchema(string",
        "System.Data.DataTable.WriteXml(string",
        "System.Data.DataTable.WriteXmlSchema(string",
        "System.Data.DataTable.ReadXml(string",
        "System.Data.DataTable.ReadXmlSchema(string",
        "System.Security.Cryptography.X509Certificates.X509Store.Add",
        "System.Security.Cryptography.X509Certificates.X509Store.Remove",
        "System.Security.Cryptography.CngKey.Create",
        "System.Security.Cryptography.CngKey.Delete",
        "System.Security.Cryptography.CspParameters",
        "System.Net.Http.HttpClient",
        "System.Net.Http.HttpMessageInvoker",
        "System.Net.Http.HttpMessageHandler",
        "System.Net.Http.SocketsHttpHandler",
        "System.Net.Http.DelegatingHandler",
        "System.Net.Http.WinHttpHandler",
        "System.Net.WebClient",
        "System.Net.WebRequest",
        "System.Net.HttpWebRequest",
        "System.Net.FtpWebRequest",
        "System.Net.FileWebRequest",
        "System.Net.HttpListener",
        "System.Net.Dns",
        "System.Net.Sockets.",
        "System.Net.WebSockets.",
        "System.Net.Mail.",
        "System.Net.NetworkInformation.",
        "System.Net.Quic.",
        "System.Net.Security.",
        // Online revocation checking fetches the CRL/OCSP URLs a certificate names.
        "System.Security.Cryptography.X509Certificates.X509Chain.Build",
        "System.Security.Cryptography.X509Certificates.X509Certificate2.Verify",
    };

    /// <summary>
    /// The deny-by-namespace form of the network rule: every type and member under these
    /// namespaces moves data off the machine (or exists only to configure something that
    /// does), apart from the pure values in <see cref="NetworkValueSymbolPrefixes"/>. A host
    /// whose tier is a trust boundary (SwarmSpan's read-only peers) applies it with those
    /// exemptions, so a networking type added by a later runtime is covered without a list
    /// update.
    /// </summary>
    public static readonly string[] NetworkNamespaceSymbolPrefixes =
    {
        "System.Net.",
    };

    /// <summary>
    /// Types from the networking namespaces that perform no I/O: addresses, status codes,
    /// encoders, and header/media-type parsers. Exempt from <see cref="NetworkNamespaceSymbolPrefixes"/>.
    /// </summary>
    public static readonly string[] NetworkValueSymbolPrefixes =
    {
        "System.Net.IPAddress",
        "System.Net.IPNetwork",
        "System.Net.IPEndPoint",
        "System.Net.WebUtility",
        "System.Net.HttpStatusCode",
        "System.Net.Cookie",
        "System.Net.Mime.",
        "System.Net.Http.Headers.",
        "System.Net.Http.HttpMethod",
    };

    /// <summary>
    /// Fully-qualified reflection operations whose target behavior cannot be recovered from the call
    /// site. Capability-aware hosts classify these as objective-authorized work; pure-compute hosts
    /// block them. Public metadata discovery such as Type.GetMethods or Assembly.GetTypes is allowed.
    /// </summary>
    public static readonly string[] ReflectionInvocationSymbolPrefixes =
    {
        "System.Activator.CreateInstance",
        "System.Reflection.MethodBase.Invoke",
        "System.Reflection.ConstructorInfo.Invoke",
        "System.Reflection.PropertyInfo.GetValue",
        "System.Reflection.PropertyInfo.SetValue",
        "System.Reflection.FieldInfo.GetValue",
        "System.Reflection.FieldInfo.SetValue",
        "System.Reflection.EventInfo.AddEventHandler",
        "System.Reflection.EventInfo.RemoveEventHandler",
        "System.Delegate.DynamicInvoke",
        "System.Reflection.MethodInfo.CreateDelegate",
        // Other routes from public metadata to a call the symbol pass cannot see.
        "System.Delegate.CreateDelegate",
        "System.Type.InvokeMember",
        "System.Reflection.MethodInvoker",
        "System.Reflection.ConstructorInvoker",
        "System.AppDomain.CreateInstance",
        "System.Linq.Expressions.LambdaExpression.Compile",
        "System.Linq.Expressions.Expression<TDelegate>.Compile",
        "Microsoft.CSharp.RuntimeBinder.Binder",
        "System.Runtime.CompilerServices.CallSite",
    };
}
