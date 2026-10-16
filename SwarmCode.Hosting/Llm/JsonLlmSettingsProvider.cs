using SwarmCore.LlmRouting.Services;

namespace SwarmCode.Hosting.Llm;

/// <summary>Host path adapter for the shared checked JSON settings store.</summary>
public sealed class JsonLlmSettingsProvider(SwarmCodeDataPaths paths)
    : JsonFileLlmSettingsProvider(paths.SettingsPath);
