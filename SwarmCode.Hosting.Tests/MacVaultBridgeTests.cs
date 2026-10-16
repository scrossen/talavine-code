using System.Xml.Linq;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SwarmCore.Secrets;
using Talavine.TestSupport;
using SwarmCode.Hosting.Mcp;
using SwarmCode.Hosting.Secrets;
using SwarmCore.Missions;

namespace SwarmCode.Hosting.Tests;

public sealed class MacVaultBridgeTests : TempDirTest
{
    [Fact]
    public async Task Encrypted_host_vault_is_read_by_brokered_MCP_tools_without_plaintext_persistence_or_results()
    {
        const string value = "synthetic-broker-mcp-value-9876";
        var keyStore = new EncryptedTestKeyStore();
        var hostVault = new SqliteSecretVault(Paths.SecretsDbPath,
            SwarmCodeHostComposer.CreateVaultProtector(keyStore, NullLogger<EnvelopeSecretProtector>.Instance, true));
        await hostVault.SetAsync(new() { Name = "fixture", Value = value });
        // Transport and composition proof; Darwin peer identity is a separate native runtime gate.
        await using var broker = new KeyStoreBrokerServer(keyStore, Paths.RootDirectory, _ => true, NullLogger<KeyStoreBrokerServer>.Instance);
        Assert.True(await broker.StartAsync());
        var helperStore = new BrokeredKeyStore(Paths.RootDirectory, (_, endpoint) => endpoint.ProcessId == Environment.ProcessId);
        var services = new ServiceCollection(); services.AddLogging();
        services.AddSingleton<IOsKeyStore>(helperStore);
        services.AddSingleton(SwarmCodeHostComposer.CreateVaultProtector(helperStore, NullLogger<EnvelopeSecretProtector>.Instance, true));
        services.AddSwarmCodeSecrets(Paths);
        services.AddOptions<SandboxOptions>().Configure(o => o.Root = Tmp);
        services.AddSingleton<ISandboxPathResolver, SandboxPathResolver>(); services.AddSingleton<ShellExecTool>();
        await using var provider = services.BuildServiceProvider();
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var listed = JsonSerializer.Serialize(await new SwarmCore.Coding.Tools.CredentialsListTool(provider).Descriptor
            .Invoke(new SwarmCore.Coding.Tools.CredentialsListTool.Input(), default), json);
        Assert.Contains("{{secret:fixture}}", listed); Assert.DoesNotContain(value, listed);
        var result = JsonSerializer.Serialize(await provider.GetRequiredService<ShellExecTool>().Descriptor
            .Invoke(new ShellExecTool.Input { Command = "echo {{secret:fixture}}" }, default), json);
        using var document = JsonDocument.Parse(result);
        Assert.Equal(0, document.RootElement.GetProperty("exitCode").GetInt32());
        Assert.Contains("{{secret:fixture}}", document.RootElement.GetProperty("stdout").GetString());
        Assert.DoesNotContain(value, result);
        foreach (var file in Directory.EnumerateFiles(Paths.RootDirectory, "secrets.db*"))
            Assert.DoesNotContain(value, System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(file)));
    }

    [Fact]
    public async Task Unavailable_Mac_key_access_never_writes_plaintext_or_replaces_existing_ciphertext()
    {
        var protector = SwarmCodeHostComposer.CreateVaultProtector(new NoOpKeyStore(), NullLogger<EnvelopeSecretProtector>.Instance, true);
        var vault = new SqliteSecretVault(Paths.SecretsDbPath, protector);
        await Assert.ThrowsAsync<InvalidOperationException>(() => vault.SetAsync(new() { Name = "synthetic", Value = "vault-synthetic-value-1234" }));
        Assert.False(File.Exists(Paths.SecretsDbPath));
        Assert.Empty(protector.Unprotect("pt:legacy-plaintext"));
        var available = SwarmCodeHostComposer.CreateVaultProtector(new EncryptedTestKeyStore(), NullLogger<EnvelopeSecretProtector>.Instance, true);
        var original = new SqliteSecretVault(Paths.SecretsDbPath, available);
        await original.SetAsync(new() { Name = "synthetic", Value = "original-synthetic-1234" });
        var bytes = File.ReadAllBytes(Paths.SecretsDbPath);
        await Assert.ThrowsAsync<InvalidOperationException>(() => vault.SetAsync(new() { Name = "synthetic", Value = "replacement-synthetic-1234" }));
        Assert.Equal(bytes, File.ReadAllBytes(Paths.SecretsDbPath));
        Assert.Equal("original-synthetic-1234", await original.GetValueAsync("synthetic"));
    }

    [Fact]
    public void Mac_bundle_builds_a_self_contained_bridge_before_signing()
    {
        var document = XDocument.Load(Path.Combine(SourceTree.Root, "SwarmCode.App", "SwarmCode.App.csproj"));
        var target = Assert.Single(document.Root!.Elements("Target").Where(t => (string?)t.Attribute("Name") == "BundleMacVaultBridge"));
        Assert.Contains("_CodesignAppBundle", (string?)target.Attribute("BeforeTargets"));
        var source = target.ToString();
        Assert.Contains("osx-arm64", source);
        Assert.Contains("SelfContained=true", source);
        Assert.Contains("Contents/Resources/SwarmCodeBridge", source);
        Assert.Contains("sign-swarmcode-bridge.sh", source);
        Assert.Contains("$(AppBundleDir)", source);
        Assert.DoesNotContain("$(ApplicationTitle).app", source);
    }

    [Fact]
    public void Catalyst_app_executable_is_not_mistaken_for_the_console()
    {
        var contents = Path.Combine(Tmp, "SwarmCode.app", "Contents");
        var app = Path.Combine(contents, "MacOS", "SwarmCode.App");
        var helper = Path.Combine(contents, MacVaultBridge.RelativeHelperPath);
        Directory.CreateDirectory(Path.GetDirectoryName(app)!);
        Directory.CreateDirectory(Path.GetDirectoryName(helper)!);
        File.WriteAllText(app, "app");
        File.WriteAllText(helper, "console");
        var launch = McpBridgeLocator.Resolve(Paths, Path.Combine(contents, "MonoBundle"), app, false);
        Assert.Equal(Path.GetFullPath(helper), launch!.Command);
    }

    [Fact]
    public void Locator_prefers_its_bundled_closure_and_peer_policy_refuses_other_helpers_and_outside_apps()
    {
        var contents = Path.Combine(Tmp, "SwarmCode.app", "Contents");
        var baseDirectory = Path.Combine(contents, "MonoBundle"); Directory.CreateDirectory(baseDirectory);
        var helper = Path.Combine(contents, MacVaultBridge.RelativeHelperPath); Directory.CreateDirectory(Path.GetDirectoryName(helper)!);
        File.WriteAllText(helper, "fixture executable");
        var external = Path.Combine(Paths.RootDirectory, "bin"); Directory.CreateDirectory(external); File.WriteAllText(Path.Combine(external, "swarmcode"), "external");
        var launch = McpBridgeLocator.Resolve(Paths, baseDirectory, null, false);
        Assert.Equal(Path.GetFullPath(helper), launch!.Command); Assert.Equal(["--mcp-server"], launch.Arguments);
        Assert.False(MacVaultBridge.IsOwnedAppExecutable(contents, helper));
        var app = Path.Combine(contents, "MacOS", "SwarmCode"); Directory.CreateDirectory(Path.GetDirectoryName(app)!); File.WriteAllText(app, "app");
        File.WriteAllText(Path.Combine(contents, "Info.plist"), "<plist><dict><key>CFBundleExecutable</key><string>SwarmCode</string></dict></plist>");
        Assert.True(MacVaultBridge.IsOwnedAppExecutable(contents, app));
        var sibling = Path.Combine(contents, "MacOS", "OtherHelper"); File.WriteAllText(sibling, "sibling");
        Assert.False(MacVaultBridge.IsOwnedAppExecutable(contents, sibling));
        Assert.False(MacVaultBridge.IsOwnedAppExecutable(contents, Path.Combine(Tmp, "other.app", "Contents", "MacOS", "SwarmCode")));
    }

    [Fact]
    public void Indented_Apple_xml_plist_authenticates_the_primary_executable()
    {
        var contents = Path.Combine(Tmp, "Owned.app", "Contents");
        var app = Path.Combine(contents, "MacOS", "SwarmCode.App");
        Directory.CreateDirectory(Path.GetDirectoryName(app)!);
        File.WriteAllText(app, "fixture executable");
        var plist = Path.Combine(contents, "Info.plist");
        File.WriteAllText(plist, """
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
                <key>CFBundleIdentifier</key>
                <string>com.example.fixture</string>
                <key>CFBundleExecutable</key>
                <!-- Normal formatting must not obscure the executable value. -->
                <string>SwarmCode.App</string>
            </dict>
            </plist>
            """);

        Assert.Equal("SwarmCode.App", AppleBundleExecutable.Read(plist));
        Assert.True(MacVaultBridge.IsOwnedAppExecutable(contents, app));
        Assert.False(MacVaultBridge.IsOwnedAppExecutable(contents, Path.Combine(contents, "MacOS", "Other")));
    }

    [Theory]
    [InlineData("<key>Other</key><string>Other</string>")]
    [InlineData("unexpected text")]
    [InlineData("<integer>1</integer>")]
    public void Xml_identity_never_skips_an_intervening_value_or_key(string intervening)
    {
        var plist = Path.Combine(Tmp, "Info.plist");
        File.WriteAllText(plist, $"<plist><dict><key>CFBundleExecutable</key>\n{intervening}\n<string>Primary</string></dict></plist>");
        Assert.Null(AppleBundleExecutable.Read(plist));
    }

    [Fact]
    public void Binary_plist_primary_identity_is_supported_and_malformed_metadata_fails_closed()
    {
        var file = Path.Combine(Tmp, "Info.plist");
        // Apple binary plist: top dictionary maps CFBundleExecutable to SwarmCode.
        var objects = new List<byte>("bplist00"u8.ToArray()) { 0xD1, 1, 2, 0x5F, 0x10, 18 };
        objects.AddRange(System.Text.Encoding.ASCII.GetBytes("CFBundleExecutable"));
        objects.Add(0x59); objects.AddRange("SwarmCode"u8.ToArray());
        var table = objects.Count; objects.AddRange(new byte[] { 8, 11, 32 });
        var trailer = new byte[32]; trailer[6] = 1; trailer[7] = 1; trailer[15] = 3; trailer[31] = (byte)table;
        objects.AddRange(trailer); File.WriteAllBytes(file, objects.ToArray());
        Assert.Equal("SwarmCode", AppleBundleExecutable.Read(file));
        File.WriteAllBytes(file, "bplist00"u8.ToArray()); Assert.Null(AppleBundleExecutable.Read(file));
        File.WriteAllText(file, "<plist><dict><key>CFBundleExecutable</key><string>A</string><key>CFBundleExecutable</key><string>B</string></dict></plist>");
        Assert.Null(AppleBundleExecutable.Read(file));
    }

    [Fact]
    public void Binary_plist_duplicate_primary_key_is_refused_even_when_first_value_is_not_a_string()
    {
        var objects = new List<byte>("bplist00"u8.ToArray()) { 0xD2, 1, 1, 2, 3, 0x5F, 0x10, 18 };
        objects.AddRange(System.Text.Encoding.ASCII.GetBytes("CFBundleExecutable"));
        objects.Add(0); objects.Add(0x59); objects.AddRange("SwarmCode"u8.ToArray());
        var table = objects.Count; objects.AddRange(new byte[] { 8, 13, 34, 35 });
        var trailer = new byte[32]; trailer[6] = 1; trailer[7] = 1; trailer[15] = 4; trailer[31] = (byte)table;
        objects.AddRange(trailer); var file = Path.Combine(Tmp, "Info.plist"); File.WriteAllBytes(file, objects.ToArray());
        Assert.Null(AppleBundleExecutable.Read(file));
    }

    [SwarmCore.Testing.SymbolicLinkFact]
    public void Bundle_symlink_leaf_and_parent_cannot_authorize_outside_executables()
    {
        var contents = Path.Combine(Tmp, "Owned.app", "Contents"); Directory.CreateDirectory(contents);
        File.WriteAllText(Path.Combine(contents, "Info.plist"), "<plist><dict><key>CFBundleExecutable</key><string>Primary</string></dict></plist>");
        var outside = Path.Combine(Tmp, "outside"); Directory.CreateDirectory(outside); File.WriteAllText(Path.Combine(outside, "Primary"), "outside app");
        var main = Path.Combine(contents, "MacOS"); Directory.CreateSymbolicLink(main, outside);
        Assert.False(MacVaultBridge.IsOwnedAppExecutable(contents, Path.Combine(main, "Primary")));
        Directory.Delete(main); Directory.CreateDirectory(main); File.CreateSymbolicLink(Path.Combine(main, "Primary"), Path.Combine(outside, "Primary"));
        Assert.False(MacVaultBridge.IsOwnedAppExecutable(contents, Path.Combine(main, "Primary")));
        var helperFolder = Path.Combine(contents, "Helpers"); Directory.CreateSymbolicLink(helperFolder, outside);
        Assert.False(MacVaultBridge.IsUnlinkedOwnedFile(contents, Path.Combine(helperFolder, "Primary")));
    }
}
