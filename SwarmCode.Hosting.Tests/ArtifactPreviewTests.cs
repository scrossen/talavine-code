using SwarmCore.Coding.Files;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SwarmCore.LlmRouting.Configuration;

namespace SwarmCode.Hosting.Tests;

public class ArtifactPreviewTests : TempDirTest
{
    [Fact]
    public void ProviderClients_ReceiveConfiguredEndpointAndModel()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["LlmProviders:DeepSeek:Endpoint"] = "http://127.0.0.1:1234/v1",
            ["LlmProviders:DeepSeek:DefaultModel"] = "chosen"
        }).Build();
        var services = new ServiceCollection().AddLogging();
        services.AddSwarmCodeHost(new SwarmCodeHostOptions { RepositoryRoot = Tmp, DataPaths = Paths, Configuration = config });
        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<DeepSeekProviderOptions>>().Value;
        Assert.Equal("http://127.0.0.1:1234/v1", options.Endpoint);
        Assert.Equal("chosen", options.DefaultModel);
    }
    [Theory]
    [InlineData("note.md", "# Heading", CodingFilePreviewKind.Markdown)]
    [InlineData("page.html", "<h1>Preview</h1>", CodingFilePreviewKind.Html)]
    [InlineData("code.cs", "class Example {}", CodingFilePreviewKind.Text)]
    [InlineData("data.csv", "name,value\na,1", CodingFilePreviewKind.Text)]
    public async Task TextFormats_KeepTheirContent(string name, string content, CodingFilePreviewKind kind)
    {
        var path = Path.Combine(Tmp, name);
        await File.WriteAllTextAsync(path, content);
        var preview = await CodingFilePreviews.ReadAsync(path);
        Assert.Equal(kind, preview.Kind);
        Assert.Equal(content, preview.Text);
    }

    [Fact]
    public async Task BinaryAndOversizedFiles_OfferExternalOpen_JsonIsFormatted()
    {
        var binary = Path.Combine(Tmp, "binary.bin");
        await File.WriteAllBytesAsync(binary, [0xff, 0, 0xfe]);
        Assert.Equal(CodingFilePreviewKind.Unsupported, (await CodingFilePreviews.ReadAsync(binary)).Kind);
        var large = Path.Combine(Tmp, "large.txt");
        using (var file = File.Create(large)) file.SetLength(CodingFilePreviews.MaxBytes + 1L);
        Assert.Equal(CodingFilePreviewKind.Unsupported, (await CodingFilePreviews.ReadAsync(large)).Kind);
        var json = Path.Combine(Tmp, "data.json");
        await File.WriteAllTextAsync(json, "{\"value\":1}");
        Assert.Contains("\n", (await CodingFilePreviews.ReadAsync(json)).Text);
        await File.WriteAllTextAsync(json, "{malformed");
        Assert.Equal("{malformed", (await CodingFilePreviews.ReadAsync(json)).Text);
    }

    [Fact]
    public void FileReferences_AreBoundedAndStructural()
    {
        Assert.Equal(["a.md", "b.json"], CodingFilePreviews.References("{\"path\":\"a.md\",\"nested\":{\"file_path\":\"b.json\"},\"command\":\"open secret.txt\"}"));
        Assert.Empty(CodingFilePreviews.References("broken"));
        Assert.Empty(CodingFilePreviews.References("{\"path\":123}"));
    }

    [Fact]
    public async Task ModelChoices_AreRememberedPerLaneAndSession()
    {
        var catalog = new WorkspaceSessionCatalog(Paths);
        var session = await catalog.CreateAsync(Tmp);
        var other = await catalog.CreateAsync(Tmp);
        await catalog.SetModelAsync(session.Id, "claude", "model-a");
        await catalog.SetModelAsync(session.Id, "codex", "model-b");
        var reopened = new WorkspaceSessionCatalog(Paths);
        Assert.Equal("model-a", (await reopened.GetAsync(session.Id))!.ModelChoices["claude"]);
        Assert.Equal("model-b", (await reopened.GetAsync(session.Id))!.ModelChoices["codex"]);
        Assert.Empty((await reopened.GetAsync(other.Id))!.ModelChoices);
    }
}
