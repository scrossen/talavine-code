using System.Text.Json;
using System.Text.RegularExpressions;

namespace SwarmCore.Agent.BusinessDispatch;

/// <summary>Owner-verified conservative per-send charges, including carrier fees. Not a provider invoice guarantee.</summary>
public sealed record BusinessSmsCostMetadata(string AccountId, string Sender, string Currency,
    decimal MaxSmsSegmentCost, decimal? MaxMmsMessageCost, string[] DestinationPrefixes,
    string EvidenceUrl, string EvidenceSha256, string VerifiedByUserId, DateTime VerifiedUtc,
    DateTime ValidUntilUtc, bool IncludesAllSendFees);

public sealed record BusinessSmsCostEstimate(string Currency, int Units, decimal Reservation, bool IsMms);

public static class BusinessSmsCostContract
{
    public static BusinessSmsCostMetadata Parse(string? json, string account, string sender, string owner, DateTime now)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > 4000)
            throw new InvalidOperationException("Verify the SMS account's fee-inclusive price ceiling and validity in Review and controls. Unknown pricing cannot reserve a free message.");
        BusinessSmsCostMetadata value;
        try { value = JsonSerializer.Deserialize<BusinessSmsCostMetadata>(json) ?? throw new JsonException(); }
        catch (JsonException) { throw new InvalidOperationException("Saved SMS pricing cannot be verified. Review the account's pricing configuration."); }
        if (value.AccountId != account || value.Sender != sender || value.VerifiedByUserId != owner
            || !BusinessSmsContract.IsSid(account, "AC") || !BusinessSmsContract.IsExactNumber(sender)
            || string.IsNullOrWhiteSpace(owner) || !Regex.IsMatch(value.Currency ?? "", "^[A-Z]{3}$")
            || value.MaxSmsSegmentCost is <= 0 or > 1000 || value.MaxMmsMessageCost is <= 0 or > 10000
            || !value.IncludesAllSendFees || value.DestinationPrefixes is not { Length: > 0 and <= 50 }
            || value.DestinationPrefixes.Any(p => p is null || !Regex.IsMatch(p, @"^\+[1-9][0-9]{0,14}$"))
            || value.DestinationPrefixes.Distinct(StringComparer.Ordinal).Count() != value.DestinationPrefixes.Length
            || value.EvidenceUrl is not { Length: > 0 and <= 2000 }
            || !Uri.TryCreate(value.EvidenceUrl, UriKind.Absolute, out var evidence) || evidence.Scheme != "https"
            || evidence.UserInfo.Length != 0 || evidence.Query.Length != 0 || evidence.Fragment.Length != 0
            || !Regex.IsMatch(value.EvidenceSha256 ?? "", "^[0-9A-F]{64}$")
            || value.VerifiedUtc.Kind != DateTimeKind.Utc || value.ValidUntilUtc.Kind != DateTimeKind.Utc
            || value.VerifiedUtc > now || value.ValidUntilUtc <= now
            || value.ValidUntilUtc <= value.VerifiedUtc || value.ValidUntilUtc > value.VerifiedUtc.AddDays(7))
            throw new InvalidOperationException("Verify current owner/account/sender pricing, currency, destination scope, all per-send fees, evidence and an expiry within seven days. Keep SMS paused until this is complete.");
        return value;
    }

    public static BusinessSmsCostEstimate Estimate(BusinessSmsCostMetadata price, string target, string body, int mediaCount = 0)
    {
        if (!BusinessSmsContract.IsExactNumber(target) || !price.DestinationPrefixes.Any(p => target.StartsWith(p, StringComparison.Ordinal)))
            throw new InvalidOperationException("This exact recipient is outside the verified pricing destination scope. Verify its fee ceiling before preparing a message.");
        if (mediaCount is < 0 or > 10 || string.IsNullOrEmpty(body) || body.Length > 1600)
            throw new InvalidOperationException("The frozen SMS/MMS content is outside supported price estimation limits.");
        int units;
        decimal cost;
        if (mediaCount > 0)
        {
            if (price.MaxMmsMessageCost is not > 0)
                throw new InvalidOperationException("Verify a separate fee-inclusive MMS message ceiling. Media cannot use an SMS segment price or silently fall back to SMS.");
            units = 1; cost = price.MaxMmsMessageCost.Value;
        }
        else
        {
            // Conservative multipart capacities leave room for carrier headers and GSM escapes/surrogate boundaries.
            // Twilio documents 142 GSM /64 Unicode carrier exceptions; use 140/62 rather than the usual153/67.
            const string gsm = "@£$¥èéùìòÇ\nØø\rÅåΔ_ΦΓΛΩΠΨΣΘΞÆæßÉ !\"#¤%&'()*+,-./0123456789:;<=>?¡ABCDEFGHIJKLMNOPQRSTUVWXYZÄÖÑÜ§¿abcdefghijklmnopqrstuvwxyzäöñüà";
            const string extended = "\f^{}\\[~]|€";
            var septets = 0; var unicode = false;
            foreach (var ch in body)
            {
                if (gsm.Contains(ch)) septets++;
                else if (extended.Contains(ch)) septets += 2;
                else unicode = true;
            }
            // UTF-16 length counts both units of supplementary characters; never normalize or rewrite the frozen body.
            units = unicode ? body.Length <= 70 ? 1 : (body.Length + 61) / 62
                : septets <= 160 ? 1 : (septets + 139) / 140;
            cost = checked(units * price.MaxSmsSegmentCost);
        }
        // Ledger decimal precision is four places. Always round upwards, never erase a fractional fee.
        return new(price.Currency, units, decimal.Ceiling(cost * 10000m) / 10000m, mediaCount > 0);
    }
}
