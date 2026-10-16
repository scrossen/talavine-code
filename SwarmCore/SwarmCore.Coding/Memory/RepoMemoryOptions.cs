namespace SwarmCore.Coding.Memory;

/// <summary>
/// Repository memory: the repository's own markdown knowledge (an index file such as <c>MEMORY.md</c> plus
/// topic files under a directory such as <c>memory/</c>), kept in git. The harness loads the index as a bounded,
/// clearly truncated block, offers read-only search/read of topic files on demand, and only ever PROPOSES edits
/// as reviewable patches; it never writes or commits repository memory on its own.
/// </summary>
public sealed class RepoMemoryOptions
{
    /// <summary>Master switch for the index injector and the repository-memory tools.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Index file at the repository root.</summary>
    public string IndexFileName { get; set; } = "MEMORY.md";

    /// <summary>Directory (relative to the repository root) that holds topic files (<c>*.md</c>).</summary>
    public string TopicDirectory { get; set; } = "memory";

    /// <summary>Maximum characters of the index injected per turn.</summary>
    public int MaxIndexCharacters { get; set; } = 6_000;

    /// <summary>Maximum lines of the index injected per turn.</summary>
    public int MaxIndexLines { get; set; } = 120;

    /// <summary>Maximum characters returned by one read.</summary>
    public int MaxReadCharacters { get; set; } = 24_000;

    /// <summary>Maximum search hits returned.</summary>
    public int MaxSearchResults { get; set; } = 20;

    /// <summary>Files larger than this are skipped by search and refused by read.</summary>
    public long MaxFileBytes { get; set; } = 4 * 1024 * 1024;

    /// <summary>Maximum characters of new text in one proposed edit.</summary>
    public int MaxProposalCharacters { get; set; } = 12_000;
}
