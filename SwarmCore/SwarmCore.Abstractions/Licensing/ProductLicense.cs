using System.Text;
using System.Text.Json;
using SwarmCore.Abstractions.Packaging;

namespace SwarmCore.Abstractions.Licensing;

/// <summary>
/// A publisher-signed, offline-verifiable license for a Swarm family product. The issuing service
/// (for example the website's purchase webhook) signs it with a product license key whose public
/// half is compiled into the app, so activation needs no server round-trip and a forged or edited
/// key never verifies. License keys are deliberately separate from release-signing keys.
/// </summary>
public sealed record ProductLicense
{
    /// <summary>Stable product id, e.g. <c>swarmspan</c>. A key for one product never activates another.</summary>
    public string Product { get; init; } = string.Empty;

    /// <summary>Opaque unique id for support and revocation lists.</summary>
    public string LicenseId { get; init; } = string.Empty;

    /// <summary>Purchaser display identity (normally the checkout email).</summary>
    public string LicensedTo { get; init; } = string.Empty;

    /// <summary><see cref="ProductLicenseKinds"/> value.</summary>
    public string Kind { get; init; } = ProductLicenseKinds.Purchase;

    /// <summary>Payment-provider reference (for example a checkout session id) for support lookups.</summary>
    public string OrderReference { get; init; } = string.Empty;

    public DateTime IssuedAtUtc { get; init; }
}

public static class ProductLicenseKinds
{
    public const string Purchase = "purchase";
    public const string Complimentary = "complimentary";
}

/// <summary>
/// Encodes licenses as <c>PL1.&lt;base64url json&gt;.&lt;base64url signature&gt;</c>. The signature covers
/// <c>"swarmcore.product-license.v1\n" + json</c> (the exact transmitted JSON bytes), so it cannot be
/// replayed as any other signed document type even if a key were ever shared.
/// </summary>
public static class ProductLicenseCodec
{
    public const string KeyPrefix = "PL1";
    private const string Purpose = "swarmcore.product-license.v1";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string Issue(ProductLicense license, string privateKeyPem)
    {
        ArgumentNullException.ThrowIfNull(license);
        ArgumentException.ThrowIfNullOrWhiteSpace(license.Product);
        ArgumentException.ThrowIfNullOrWhiteSpace(license.LicenseId);

        var json = JsonSerializer.Serialize(license, JsonOptions);
        var signature = PublisherSigning.Sign(SignedDocument(json), privateKeyPem);
        return string.Join('.', KeyPrefix, Base64Url(Encoding.UTF8.GetBytes(json)), Base64Url(Convert.FromBase64String(signature)));
    }

    /// <summary>
    /// Verifies a pasted or linked key. Tolerates surrounding whitespace and line breaks introduced by
    /// email clients. Never throws; returns a user-presentable failure reason instead.
    /// </summary>
    public static ProductLicenseVerification Verify(string? licenseKey, string expectedProduct, string publicKeyPem)
    {
        if (string.IsNullOrWhiteSpace(licenseKey))
        {
            return ProductLicenseVerification.Failed("Enter a license key.");
        }

        var compact = string.Concat(licenseKey.Where(c => !char.IsWhiteSpace(c)));
        var parts = compact.Split('.');
        if (parts.Length != 3 || !string.Equals(parts[0], KeyPrefix, StringComparison.Ordinal))
        {
            return ProductLicenseVerification.Failed("That doesn't look like a license key.");
        }

        string json;
        string signatureBase64;
        try
        {
            json = Encoding.UTF8.GetString(FromBase64Url(parts[1]));
            signatureBase64 = Convert.ToBase64String(FromBase64Url(parts[2]));
        }
        catch (FormatException)
        {
            return ProductLicenseVerification.Failed("That license key is incomplete. Copy the whole key and try again.");
        }

        if (!PublisherSigning.Verify(SignedDocument(json), signatureBase64, publicKeyPem))
        {
            return ProductLicenseVerification.Failed("That license key isn't valid.");
        }

        ProductLicense? license;
        try
        {
            license = JsonSerializer.Deserialize<ProductLicense>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return ProductLicenseVerification.Failed("That license key isn't valid.");
        }

        if (license is null || string.IsNullOrWhiteSpace(license.LicenseId))
        {
            return ProductLicenseVerification.Failed("That license key isn't valid.");
        }

        if (!string.Equals(license.Product, expectedProduct, StringComparison.OrdinalIgnoreCase))
        {
            return ProductLicenseVerification.Failed("That license key is for a different product.");
        }

        return new ProductLicenseVerification(true, license, compact, null);
    }

    private static string SignedDocument(string json) => Purpose + "\n" + json;

    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string value)
    {
        var s = value.Replace('-', '+').Replace('_', '/');
        s = s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=');
        return Convert.FromBase64String(s);
    }
}

/// <param name="NormalizedKey">The whitespace-stripped key to persist when valid.</param>
public sealed record ProductLicenseVerification(bool IsValid, ProductLicense? License, string? NormalizedKey, string? FailureReason)
{
    public static ProductLicenseVerification Failed(string reason) => new(false, null, null, reason);
}
