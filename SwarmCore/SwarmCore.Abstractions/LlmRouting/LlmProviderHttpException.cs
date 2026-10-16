using System.Net;
namespace SwarmCore.Abstractions.LlmRouting;

/// <summary>Safe provider diagnostic with a transport-owned failure category.</summary>
public sealed class LlmProviderHttpException(string message, HttpStatusCode status, LlmFailureCategory category)
    : HttpRequestException(message, null, status)
{
    public LlmFailureCategory Category { get; } = category;
}
