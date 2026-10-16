namespace SwarmCore.Abstractions.Text;

/// <summary>A read-only dictionary service. Offsets are UTF-16; checking never edits a document.</summary>
public interface ISpellingChecker
{
    Task<IReadOnlyList<SpellingIssue>> CheckAsync(string text, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> SuggestAsync(string word, string language, CancellationToken cancellationToken = default);
}

public sealed record SpellingIssue(int Index, int Length, string Word, string Language);
