namespace SwarmCore.LlmRouting;

/// <summary>
/// The provider completed a physical call, but the caller's deterministic
/// artifact contract rejected the response. No response content is retained in
/// the exception because model output may echo private prompt material.
/// </summary>
public sealed class LlmResponseContractException : InvalidOperationException
{
    public LlmResponseContractException(string functionType, string provider, string model)
        : base(
            $"{provider}/{model} returned a response that did not satisfy the complete "
            + $"artifact contract for {functionType}.")
    {
        FunctionType = functionType;
        Provider = provider;
        Model = model;
    }

    public string FunctionType { get; }
    public string Provider { get; }
    public string Model { get; }
}
