using System.IO.Compression;
using System.Text;
using SwarmCore.Coding.Files;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SwarmCore.Abstractions.LlmRouting;

namespace SwarmCode.Hosting.Tests;

public class AttachmentTests : TempDirTest
{
    [Fact]
    public async Task DefaultLimits_AcceptFilesAboveTwentyMbAndVerifyFromDisk()
    {
        var sourcePath = Path.Combine(Tmp, "large-source.bin");
        using (var source = File.Create(sourcePath)) source.SetLength(21L * 1024 * 1024);
        await using var input = File.OpenRead(sourcePath);
        var file = await new AttachmentStore(Paths).ImportAsync("session", "large.bin", input);
        Assert.Equal(21L * 1024 * 1024, file.Length);
        await CodingAttachments.VerifyAsync(file);
        await Assert.ThrowsAsync<InvalidDataException>(() => CodingAttachments.VerifyAsync(file, limits: new() { MaxFileBytes = 1024 }));
    }

    [Fact]
    public void OptionalBudgets_PersistWithoutOverwritingOtherSettings()
    {
        var settings = new UserSettingsFile(Paths);
        settings.WriteValue("LlmProviders", "preserve", "existing");
        var limits = new CodingAttachmentOptions { MaxFileBytes = 1048576, MaxTotalBytes = 2097152, MaxFiles = 2, MaxTextCharacters = 1000 };
        settings.WriteCodingSection("AttachmentLimits", limits);
        var restored = settings.ReadCodingSection<CodingAttachmentOptions>("AttachmentLimits");
        Assert.Equal(1048576, restored.MaxFileBytes);
        Assert.Equal("existing", settings.ReadValue("LlmProviders", "preserve"));
        restored.ValidateBatch(2, 2097152);
        Assert.Throws<InvalidDataException>(() => restored.ValidateBatch(3, 1));
        Assert.Throws<InvalidDataException>(() => restored.ValidateBatch(1, 2097153));
        Assert.Throws<InvalidDataException>(() => new CodingAttachmentOptions { MaxFiles = -1 }.Validate());
        new CodingAttachmentOptions().ValidateBatch(int.MaxValue, long.MaxValue);
    }
    [Fact]
    public void DeepSeekCapabilities_AdmitFlashImagesAndRejectProImages()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddSwarmCodeHost(new SwarmCodeHostOptions { RepositoryRoot = Tmp, DataPaths = Paths, Configuration = new ConfigurationBuilder().Build() });
        using var provider = services.BuildServiceProvider();
        var capabilities = provider.GetRequiredService<ILlmCapabilitiesService>();
        Assert.True(capabilities.SupportsVision("DeepSeek", "deepseek-flash"));
        Assert.False(capabilities.SupportsVision("DeepSeek", "deepseek-v4-pro"));
    }
    [Fact]
    public async Task Import_CopiesSanitizedNameAndDetectsImageFromBytes()
    {
        var bytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aZuoAAAAASUVORK5CYII=");
        var file = await new AttachmentStore(Paths).ImportAsync("session", "../../screen.dat", new MemoryStream(bytes));
        Assert.Equal("screen.dat", file.FileName);
        Assert.Equal("image/png", file.MediaType);
        Assert.StartsWith(Path.Combine(Paths.SessionsDirectory, "attachments", "session"), file.LocalPath);
        var parts = await CodingAttachments.ForApiAsync([file]);
        Assert.Equal(bytes, Convert.FromBase64String(Assert.Single(parts.Images).Base64Data));
        Assert.Empty(parts.Files);
    }

    [Fact]
    public async Task InstructionNamedDocuments_RemainDataInsteadOfCliConfiguration()
    {
        var file = await new AttachmentStore(Paths).ImportAsync("session", "CLAUDE.md", new MemoryStream("source"u8.ToArray()));
        Assert.Equal("CLAUDE.md", file.FileName);
        Assert.Equal("file-CLAUDE.md", Path.GetFileName(file.LocalPath));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(file.LocalPath)!, "CLAUDE.md")));
    }

    [Fact]
    public async Task Import_RejectsOversizedDataAndCleansIncompleteCopy()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => new AttachmentStore(Paths, new() { MaxFileBytes = 1024 }).ImportAsync("session", "large.bin", new MemoryStream(new byte[1025])));
        Assert.Empty(Directory.GetFiles(Path.Combine(Paths.SessionsDirectory, "attachments"), "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ChangedAttachment_IsRejectedBeforeProviderDelivery()
    {
        var file = await new AttachmentStore(Paths).ImportAsync("session", "note.md", new MemoryStream("hello"u8.ToArray()));
        await File.WriteAllTextAsync(file.LocalPath, "other");
        await Assert.ThrowsAsync<InvalidDataException>(() => CodingAttachments.ForApiAsync([file]));
    }

    [Theory]
    [InlineData("note.md", "# Heading\nAttached text")]
    [InlineData("data.csv", "name,value\na,1")]
    [InlineData("code.cs", "class C {}")]
    [InlineData("data.json", "{\"a\":1}")]
    public async Task TextFiles_AreDeliveredVerbatim(string name, string content)
    {
        var file = await new AttachmentStore(Paths).ImportAsync("session", name, new MemoryStream(Encoding.UTF8.GetBytes(content)));
        var parts = await CodingAttachments.ForApiAsync([file]);
        Assert.Equal(content, Assert.Single(parts.Files).TextContent);
        Assert.Empty(parts.Images);
    }

    [Fact]
    public async Task WordDocument_ExtractsParagraphs()
    {
        using var bytes = new MemoryStream();
        using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, true))
        {
            using var writer = new StreamWriter(zip.CreateEntry("word/document.xml").Open());
            writer.Write("<w:document xmlns:w='http://schemas.openxmlformats.org/wordprocessingml/2006/main'><w:body><w:p><w:r><w:t>First</w:t></w:r></w:p><w:p><w:r><w:t>Second</w:t></w:r></w:p></w:body></w:document>");
        }
        bytes.Position = 0;
        var file = await new AttachmentStore(Paths).ImportAsync("session", "document.docx", bytes);
        Assert.Equal("First\nSecond", Assert.Single((await CodingAttachments.ForApiAsync([file])).Files).TextContent);
    }

    [Fact]
    public async Task BinaryFiles_ShowActionableErrorInsteadOfEmptyContext()
    {
        var file = await new AttachmentStore(Paths).ImportAsync("session", "archive.zip", new MemoryStream(new byte[] { 0, 255, 0 }));
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => CodingAttachments.ForApiAsync([file]));
        Assert.Contains("native provider", error.Message);
    }

    [Fact]
    public async Task TextBudget_IsEnforced()
    {
        var file = await new AttachmentStore(Paths).ImportAsync("session", "large.txt", new MemoryStream(Encoding.UTF8.GetBytes(new string('x', 1001))));
        await Assert.ThrowsAsync<InvalidDataException>(() => CodingAttachments.ForApiAsync([file], limits: new() { MaxTextCharacters = 1000 }));
    }

    [Fact]
    public void Journal_RestoresAttachmentMetadataInJobQueueAndTranscript()
    {
        var file = new CodingAttachment("id", "note.md", "managed/path", "text/markdown", 10, "hash");
        var store = new FileConversationJobStore(Paths);
        store.Save("session", new ConversationJob {
            Attachments = [file], Queue = [new("follow up", "deepseek", false, false) { Attachments = [file] }],
            Entries = [SavedTranscriptEntry.From(new UserEntry { Text = "question", Attachments = [file] })!]
        });
        var loaded = store.Load("session")!;
        Assert.Equal(file, Assert.Single(loaded.Attachments));
        Assert.Equal(file, Assert.Single(Assert.Single(loaded.Queue).Attachments));
        Assert.Equal(file, Assert.Single(Assert.IsType<UserEntry>(Assert.Single(loaded.Entries).Restore()).Attachments));
    }
}
