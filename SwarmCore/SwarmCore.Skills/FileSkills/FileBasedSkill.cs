using System.Diagnostics;
using SwarmCore.Abstractions.LlmRouting;
using SwarmCore.Skills.Abstractions;
using SwarmCore.Skills.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace SwarmCore.Skills.FileSkills;

/// <summary>
/// An ISkill implementation loaded from a SKILL.md manifest file.
/// Executes functions by sending the skill's markdown instructions + parameters to the LLM router.
/// This bridges the file-based skill definition pattern with the existing SwarmCore skill infrastructure.
/// </summary>
public sealed class FileBasedSkill : ISkill
{
    private readonly SkillManifest _manifest;
    private readonly List<SkillFunction> _functions;
    private readonly IReadOnlyList<SkillPermission> _requiredPermissions;
    private IServiceProvider? _serviceProvider;
    private ILogger? _logger;

    public FileBasedSkill(SkillManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        _manifest = CloneManifest(manifest);
        _functions = MapFunctions(_manifest);
        // The manifest is a private copy that never changes, so the mapping is computed once
        // instead of on every read by the executor, security policy and registry.
        _requiredPermissions = _manifest.Permissions
            .Select(TryMapPermission)
            .Where(p => p is not null)
            .Cast<SkillPermission>()
            .ToList()
            .AsReadOnly();
    }

    public string SkillId => _manifest.GetSkillId();
    public string Name => _manifest.Name;
    public string Description => _manifest.Description;
    public string Version => _manifest.Version;

    public SkillCategory Category => MapCategory(_manifest.Category);
    public SkillTrustLevel TrustLevel => MapTrustLevel(_manifest.TrustLevel, _manifest.Source);

    public IReadOnlyList<SkillPermission> RequiredPermissions => _requiredPermissions;

    public IReadOnlyList<string> Keywords => _manifest.Keywords.AsReadOnly();

    /// <summary>
    /// The raw markdown instructions from the SKILL.md body.
    /// Available for agents to inject into their own prompts.
    /// </summary>
    public string Instructions => _manifest.Instructions;

    /// <summary>
    /// The underlying parsed manifest (a copy; callers cannot change this skill through it).
    /// </summary>
    public SkillManifest Manifest => CloneManifest(_manifest);

    /// <summary>The SKILL.md path, without copying the manifest.</summary>
    public string SourcePath => _manifest.SourcePath;

    /// <summary>
    /// Files bundled with this skill, discovered at load time and read only on demand.
    /// </summary>
    public IReadOnlyList<SkillResource> Resources => _manifest.Resources.AsReadOnly();

    /// <summary>
    /// True when this SKILL.md is a multi-step agent playbook rather than a
    /// one-shot prompt transform. The Agent Skills <c>allowed-tools</c> field is
    /// declarative: it names the governed host tools the outer agent turn may
    /// need, but never grants or invokes them on its own.
    /// </summary>
    public bool IsAgentWorkflow => _manifest.AllowedTools.Count > 0;

    /// <summary>
    /// Where this skill was installed from. Drives the skill-id namespace.
    /// </summary>
    public SkillSource Source => _manifest.Source;

    /// <summary>
    /// True when the package carried a <c>SIGNATURE.json</c> that verified against the
    /// pinned publisher key at load time. Unsigned packages load normally with this false;
    /// a package whose signature is present but does not verify is refused by the loader.
    /// </summary>
    public bool VerifiedPublisher => _manifest.VerifiedPublisher;

    private static SkillManifest CloneManifest(SkillManifest source)
        => new()
        {
            Name = source.Name,
            Description = source.Description,
            Version = source.Version,
            Category = source.Category,
            TrustLevel = source.TrustLevel,
            FunctionType = source.FunctionType,
            License = source.License,
            Compatibility = source.Compatibility,
            AllowedTools = source.AllowedTools.ToList(),
            Metadata = new Dictionary<string, string>(source.Metadata, StringComparer.OrdinalIgnoreCase),
            UnknownKeys = source.UnknownKeys.ToList(),
            Diagnostics = source.Diagnostics.ToList(),
            Resources = source.Resources.ToList(),
            Source = source.Source,
            VerifiedPublisher = source.VerifiedPublisher,
            Keywords = source.Keywords.ToList(),
            Functions = source.Functions
                .Select(function => new SkillManifestFunction
                {
                    Name = function.Name,
                    Description = function.Description,
                    ReturnType = function.ReturnType,
                    Parameters = function.Parameters
                        .Select(parameter => new SkillManifestParameter
                        {
                            Name = parameter.Name,
                            Type = parameter.Type,
                            Description = parameter.Description,
                            Required = parameter.Required,
                            DefaultValue = parameter.DefaultValue,
                            ValidValues = parameter.ValidValues.ToList(),
                            OptionsSource = parameter.OptionsSource,
                            DependsOn = parameter.DependsOn.ToList(),
                        })
                        .ToList(),
                })
                .ToList(),
            Permissions = source.Permissions.ToList(),
            Instructions = source.Instructions,
            SourcePath = source.SourcePath,
            Temperature = source.Temperature,
            MaxTokens = source.MaxTokens,
        };

    public IEnumerable<SkillFunction> GetFunctions() => _functions.AsReadOnly();

    public async Task<SkillResult> ExecuteAsync(
        string functionName,
        SkillExecutionContext context,
        CancellationToken ct = default)
    {
        if (_serviceProvider is null)
            return SkillResult.Fail("Skill has not been initialized");

        var manifestFunc = _manifest.Functions
            .FirstOrDefault(f => f.Name.Equals(functionName, StringComparison.OrdinalIgnoreCase));

        if (manifestFunc is null)
            return SkillResult.Fail($"Function '{functionName}' not found in skill '{Name}'");

        // A workflow that names host tools must stay in the outer tool-capable
        // agent turn. Sending these instructions to ILlmRouter.GenerateResponseAsync
        // created a tool-less nested model call: operational skills then either
        // hallucinated tool evidence or returned an unusable failure. Activation
        // is deliberately side-effect free; the outer agent calls the named tools
        // through their normal action-time policy and receipts.
        if (IsAgentWorkflow)
            return ActivateAgentWorkflow(manifestFunc, context);

        var sw = Stopwatch.StartNew();

        try
        {
            // The registry initializes skills with its root provider and the router is
            // scoped, so each execution resolves it from its own scope.
            await using var scope = _serviceProvider.CreateAsyncScope();
            var llmRouter = scope.ServiceProvider.GetRequiredService<ILlmRouter>();

            var prompt = SkillPromptBuilder.BuildExecutionPrompt(_manifest, manifestFunc, context, TrustLevel);

            var options = new LlmRouterOptions
            {
                Temperature = _manifest.Temperature ?? 0.3f,
                MaxTokens = _manifest.MaxTokens,
                ForceJson = manifestFunc.ReturnType.Equals("json", StringComparison.OrdinalIgnoreCase),
                ExpectedFormat = manifestFunc.ReturnType,
                // The executor's timeout token must reach the provider call. Without it a
                // hung model request outlived the executor's deadline and held an
                // execution slot until the process restarted.
                RequestCancellationToken = ct
            };

            var response = await llmRouter.GenerateResponseAsync(
                _manifest.FunctionType,
                prompt,
                options);

            sw.Stop();

            _logger?.LogDebug(
                "File-based skill {SkillId} function {Function} completed in {Elapsed}ms",
                SkillId, functionName, sw.ElapsedMilliseconds);

            return new SkillResult
            {
                Success = true,
                Result = response,
                ExecutionTime = sw.Elapsed,
                SkillId = SkillId,
                FunctionName = functionName,
                Metadata = new Dictionary<string, object>
                {
                    ["source"] = "file-based-skill",
                    ["sourcePath"] = _manifest.SourcePath,
                    ["functionType"] = _manifest.FunctionType
                }
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger?.LogError(ex, "File-based skill {SkillId} function {Function} failed", SkillId, functionName);
            return SkillResult.Fail(ex.Message, ex.ToString());
        }
    }

    public Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        // Available if the source file still exists (or was loaded from content, not file)
        if (string.IsNullOrEmpty(_manifest.SourcePath))
            return Task.FromResult(true);

        return Task.FromResult(File.Exists(_manifest.SourcePath));
    }

    public Task InitializeAsync(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
        _logger = serviceProvider.GetService<ILoggerFactory>()?.CreateLogger<FileBasedSkill>();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Map manifest functions to the SwarmCore SkillFunction model.
    /// </summary>
    private static List<SkillFunction> MapFunctions(SkillManifest manifest)
    {
        return manifest.Functions.Select(f => new SkillFunction
        {
            Name = f.Name,
            Description = f.Description,
            ReturnType = f.ReturnType,
            Parameters = f.Parameters.Select(p => new SkillParameter
            {
                Name = p.Name,
                Type = p.Type,
                Description = p.Description,
                IsRequired = p.Required,
                DefaultValue = p.DefaultValue,
                ValidValues = p.ValidValues.Count == 0 ? null : p.ValidValues.ToList(),
                OptionsSource = p.OptionsSource,
                DependsOn = p.DependsOn.ToList()
            }).ToList()
        }).ToList();
    }

    private static SkillCategory MapCategory(string category) =>
        category.ToLowerInvariant() switch
        {
            "email" => SkillCategory.Email,
            "data" => SkillCategory.Data,
            "communication" => SkillCategory.Communication,
            "automation" => SkillCategory.Automation,
            "security" => SkillCategory.Security,
            "knowledge" => SkillCategory.Knowledge,
            "development" => SkillCategory.Development,
            "diagnostics" => SkillCategory.Diagnostics,
            _ => SkillCategory.External
        };

    // User-writable SKILL.md files cannot self-promote beyond Community. The
    // built-in source is different: the host assigns that source while loading
    // reviewed files from the app bundle, so honoring a declared Verified level
    // there does not let a downloaded manifest grant itself trust. Even built-ins
    // are capped at Verified; Trusted/System remain code/deployment decisions.
    internal static SkillTrustLevel MapTrustLevel(string trustLevel, SkillSource source)
    {
        if (trustLevel.Equals("untrusted", StringComparison.OrdinalIgnoreCase))
            return SkillTrustLevel.Untrusted;

        return source == SkillSource.BuiltIn
               && trustLevel.Equals("verified", StringComparison.OrdinalIgnoreCase)
            ? SkillTrustLevel.Verified
            : SkillTrustLevel.Community;
    }

    /// <summary>
    /// Resolve a manifest permission id (either the <c>data.read</c> form or the legacy
    /// <c>ReadData</c> form) to its <see cref="SkillPermission"/>, or null when it names
    /// nothing this host knows. The parser uses the null case to raise a diagnostic.
    /// </summary>
    internal static SkillPermission? TryMapPermission(string permissionId) =>
        permissionId.ToLowerInvariant() switch
        {
            "readdata" or "data.read" => SkillPermission.ReadData,
            "writedata" or "data.write" => SkillPermission.WriteData,
            "sendemail" or "email.send" => SkillPermission.SendEmail,
            "reademail" or "email.read" => SkillPermission.ReadEmail,
            "networkaccess" or "network.access" => SkillPermission.NetworkAccess,
            "filesystemread" or "filesystem.read" => SkillPermission.FileSystemRead,
            "filesystemwrite" or "filesystem.write" => SkillPermission.FileSystemWrite,
            "executecode" or "code.execute" => SkillPermission.ExecuteCode,
            "readdiagnostics" or "diagnostics.read" => SkillPermission.ReadDiagnostics,
            "writeslocal" or "local.write" => SkillPermission.WritesLocal,
            _ => null
        };

    private SkillResult ActivateAgentWorkflow(
        SkillManifestFunction function,
        SkillExecutionContext context)
    {
        var arguments = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var missing = new List<string>();

        foreach (var parameter in function.Parameters)
        {
            var value = context.GetParameter<string>(parameter.Name)
                        ?? parameter.DefaultValue;
            if (string.IsNullOrWhiteSpace(value))
            {
                if (parameter.Required)
                    missing.Add(parameter.Name);
                continue;
            }

            arguments[parameter.Name] = value;
        }

        if (missing.Count > 0)
        {
            return SkillResult.Fail(
                $"Missing required input{(missing.Count == 1 ? string.Empty : "s")}: {string.Join(", ", missing)}.");
        }

        return SkillResult.Ok(new SkillWorkflowActivation
        {
            SkillId = SkillId,
            SkillName = Name,
            FunctionName = function.Name,
            FunctionDescription = function.Description,
            ExpectedReturnType = function.ReturnType,
            AllowedTools = _manifest.AllowedTools.ToList(),
            Arguments = arguments,
            WorkflowInstructions = _manifest.Instructions,
            NextStep =
                "Continue this same agent turn by following WorkflowInstructions and calling the governed tools named in AllowedTools as needed. "
                + "Do not claim the workflow succeeded until those tool results support the outcome. Do not call skills.run for this skill again."
        });
    }
}

/// <summary>
/// Side-effect-free activation returned by an operational SKILL.md. The outer
/// tool-use agent receives this object as the <c>skills.run</c> result and then
/// performs the playbook through the ordinary governed tool catalog.
/// </summary>
public sealed record SkillWorkflowActivation
{
    public required string SkillId { get; init; }
    public required string SkillName { get; init; }
    public required string FunctionName { get; init; }
    public required string FunctionDescription { get; init; }
    public required string ExpectedReturnType { get; init; }
    public required IReadOnlyList<string> AllowedTools { get; init; }
    public required IReadOnlyDictionary<string, string> Arguments { get; init; }
    public required string WorkflowInstructions { get; init; }
    public required string NextStep { get; init; }
}
