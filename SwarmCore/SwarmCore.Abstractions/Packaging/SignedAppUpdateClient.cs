using System.Text.Json;

namespace SwarmCore.Abstractions.Packaging;

/// <summary>Fetches updates using a host-pinned product, channel and publisher identity.</summary>
public sealed class SignedAppUpdateClient(HttpClient http, string appName, string channel,
    string publicKeyPem, SignedAppReleasePlatform platform)
{
    public async Task<SignedAppReleaseManifest> ReadAsync(Uri url, CancellationToken ct = default)
    {
        var bytes = await DownloadAsync(url, 1024 * 1024, ct);
        var manifest = JsonSerializer.Deserialize<SignedAppReleaseManifest>(bytes, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidDataException("The update manifest was empty.");
        Validate(manifest);
        return manifest;
    }

    public SignedAppReleaseInstallPlan Validate(SignedAppReleaseManifest manifest)
    {
        var plan = SignedAppReleaseSigning.TryBuildInstallPlan(manifest, platform, publicKeyPem, out var error)
            ?? throw new InvalidDataException(error);
        if (!string.Equals(manifest.AppName, appName, StringComparison.Ordinal)
            || !string.Equals(manifest.Channel, channel, StringComparison.Ordinal))
            throw new InvalidDataException("This update belongs to a different product or release channel.");
        if (!Version.TryParse(plan.Asset.Version, out _))
            throw new InvalidDataException("The update has an invalid platform version.");
        return plan;
    }

    public bool IsNewer(SignedAppReleaseManifest manifest, string currentVersion)
        => Version.Parse(Validate(manifest).Asset.Version) > Version.Parse(currentVersion);

    public async Task<byte[]> ReadInstallerAsync(SignedAppReleaseManifest manifest, CancellationToken ct = default)
    {
        var plan = Validate(manifest);
        var bytes = await DownloadAsync(plan.ScriptUrl, 1024 * 1024, ct);
        if (!PublisherSigning.HashesMatch(plan.ScriptSha256, PublisherSigning.Sha256Hex(bytes)))
            throw new InvalidDataException("The updater checksum did not match its signed manifest.");
        return bytes;
    }

    private async Task<byte[]> DownloadAsync(Uri url, int limit, CancellationToken ct)
    {
        if (url.Scheme != Uri.UriSchemeHttps && !(url.Scheme == Uri.UriSchemeHttp && url.IsLoopback))
            throw new InvalidDataException("Updates require HTTPS.");
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        var finalUrl = response.RequestMessage?.RequestUri;
        if (finalUrl is not null && finalUrl.Scheme != Uri.UriSchemeHttps && !finalUrl.IsLoopback)
            throw new InvalidDataException("The update download redirected away from HTTPS.");
        if (response.Content.Headers.ContentLength > limit) throw new InvalidDataException("The update response was too large.");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            if (output.Length + read > limit) throw new InvalidDataException("The update response was too large.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }
}
