using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using SwarmCore.Abstractions.Packaging;

namespace SwarmCode.App.Tests;

public class SignedAppUpdateTests
{
    private static (SignedAppReleaseManifest Manifest, string PublicKey) Release(string app = "SwarmCode", string channel = "stable")
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var manifest = new SignedAppReleaseManifest
        {
            AppName = app, Channel = channel, Version = "99.0.0",
            MacInstallScriptUrl = "https://example.com/install.sh",
            MacInstallScriptSha256 = PublisherSigning.Sha256Hex("verified-script"),
            Mac = new() { Version = "1.0.2", DownloadUrl = "https://example.com/app.dmg", Sha256 = new string('a', 64), PackageType = "dmg" }
        };
        return (manifest with { Signature = SignedAppReleaseSigning.Sign(manifest, key.ExportPkcs8PrivateKeyPem()) }, key.ExportSubjectPublicKeyInfoPem());
    }

    [Theory]
    [InlineData("Vibes", "stable")]
    [InlineData("SwarmCode", "preview")]
    public void Rejects_validly_signed_other_products_and_channels(string app, string channel)
    {
        var (manifest, key) = Release(app, channel);
        var client = new SignedAppUpdateClient(new HttpClient(), "SwarmCode", "stable", key, SignedAppReleasePlatform.Mac);
        Assert.Throws<InvalidDataException>(() => client.Validate(manifest));
    }

    [Fact]
    public void Uses_platform_version_and_rejects_tampered_assets()
    {
        var (manifest, key) = Release();
        var client = new SignedAppUpdateClient(new HttpClient(), "SwarmCode", "stable", key, SignedAppReleasePlatform.Mac);
        Assert.True(client.IsNewer(manifest, "1.0.1"));
        Assert.False(client.IsNewer(manifest, "1.0.2"));
        Assert.False(client.IsNewer(manifest, "1.0.3"));
        Assert.Throws<InvalidDataException>(() => client.Validate(manifest with { Mac = manifest.Mac with { DownloadUrl = "https://attacker.example/app.dmg" } }));
    }

    [Theory]
    [InlineData("verified-script", true)]
    [InlineData("tampered-script", false)]
    public async Task Executes_only_exact_signed_script_bytes(string script, bool accepted)
    {
        var (manifest, key) = Release();
        using var http = new HttpClient(new Handler(script));
        var client = new SignedAppUpdateClient(http, "SwarmCode", "stable", key, SignedAppReleasePlatform.Mac);
        if (accepted) Assert.Equal(script, System.Text.Encoding.UTF8.GetString(await client.ReadInstallerAsync(manifest)));
        else await Assert.ThrowsAsync<InvalidDataException>(() => client.ReadInstallerAsync(manifest));
    }

    [Fact]
    public async Task Refuses_oversized_responses_and_untrusted_manifests()
    {
        var (manifest, key) = Release();
        using var http = new HttpClient(new Handler(new string('x', 1024 * 1024 + 1)));
        var client = new SignedAppUpdateClient(http, "SwarmCode", "stable", key, SignedAppReleasePlatform.Mac);
        await Assert.ThrowsAsync<InvalidDataException>(() => client.ReadAsync(new Uri("https://example.com/latest.json")));
        using var unsignedHttp = new HttpClient(new Handler(JsonSerializer.Serialize(manifest with { Signature = "" })));
        var unsigned = new SignedAppUpdateClient(unsignedHttp, "SwarmCode", "stable", key, SignedAppReleasePlatform.Mac);
        await Assert.ThrowsAsync<InvalidDataException>(() => unsigned.ReadAsync(new Uri("https://example.com/latest.json")));
    }

    private sealed class Handler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body), RequestMessage = request });
    }
}
