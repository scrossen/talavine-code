using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using SwarmCore.Agent.Abstractions;
using SwarmCore.Agent.Catalog;
using SwarmCore.Agent.Runtime;

namespace SwarmCore.Coding.Interaction;

/// <summary>Model input for <c>user.ask</c>.</summary>
public sealed record UserAskInput(
    string Question,
    IReadOnlyList<UserQuestionOption>? Options = null,
    bool? AllowMultiple = null,
    bool? AllowFreeText = null,
    string? Context = null);

/// <summary>What the model gets back: the answer, or why there is none (answered=false).</summary>
public sealed record UserAskResult(bool Answered, string? Text, IReadOnlyList<string> Selected, string Message)
{
    public static UserAskResult From(UserAnswer answer) => new(answer.Answered, answer.Text, answer.Selected, answer.Message);
    public static UserAskResult Fail(string message) => new(false, null, [], message);
}

/// <summary>
/// The <c>user.ask</c> tool. <paramref name="owner"/> resolves the trusted calling conversation (the current turn for
/// engine lanes, the bridged native turn for CLI lanes); a model can never name the conversation it asks from.
/// Read-only on purpose: a plan turn may ask, and the native bridge serves it to plan turns too.
/// </summary>
public static class UserQuestionTools
{
    public const string OwnerAgentId = "coding.interaction";
    public const string AskToolId = "user.ask";

    public static ToolDescriptor Create(IUserQuestionBroker broker, Func<string?> owner) =>
        ToolDescriptor.Create<UserAskInput, UserAskResult>(AskToolId, OwnerAgentId,
            "Ask the person a question and wait for the answer. Use only when a decision is genuinely theirs (a product choice, "
            + "an ambiguity the code and docs cannot settle, an action with consequences they should pick); never for something you "
            + "can find out or decide yourself. Batch related questions into one call: give up to 8 options (label plus a short "
            + "description), allowMultiple for several, allowFreeText (default true) for their own words, and put background in "
            + "context. answered=false means no one can answer here or they skipped it: do not ask again; decide, state the "
            + "assumption, and continue.",
            ToolCostClass.Cheap, ToolSideEffect.ReadOnly,
            (input, ct) => AskAsync(broker, owner, input, ct),
            keywords: ["ask", "question", "user", "person", "clarify", "choose", "decision", "confirm", "input"]);

    private static async Task<UserAskResult> AskAsync(IUserQuestionBroker broker, Func<string?> owner, UserAskInput input, CancellationToken ct)
    {
        if (owner() is not { Length: > 0 } conversationId) return UserAskResult.Fail("Questions can be asked only inside a conversation turn.");
        if (TryBuild(conversationId, input, out var request, out var error) is false) return UserAskResult.Fail(error!);
        ct.ThrowIfCancellationRequested();
        // A cancelled turn propagates the cancellation so the engine closes the call; any other broker failure becomes an answer the model can act on.
        try { return UserAskResult.From(await broker.AskAsync(request!, ct).ConfigureAwait(false)); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { return UserAskResult.Fail("The question could not be shown: " + ex.Message + " " + UserAnswer.NoOneMessage); }
    }

    /// <summary>Validates and normalizes the model's input into a request; false with a message the model can read.</summary>
    public static bool TryBuild(string conversationId, UserAskInput input, out UserQuestionRequest? request, out string? error)
    {
        request = null;
        error = null;
        var question = input.Question?.Trim();
        if (string.IsNullOrEmpty(question)) { error = "question is required."; return false; }
        if (question.Length > UserQuestionRequest.MaxQuestionLength) { error = $"question must be at most {UserQuestionRequest.MaxQuestionLength} characters; put the background in context."; return false; }
        var context = string.IsNullOrWhiteSpace(input.Context) ? null : input.Context.Trim();
        if (context is { Length: > UserQuestionRequest.MaxContextLength }) { error = $"context must be at most {UserQuestionRequest.MaxContextLength} characters."; return false; }

        var options = new List<UserQuestionOption>();
        if (input.Options is { Count: > 0 } given)
        {
            if (given.Count > UserQuestionRequest.MaxOptions) { error = $"At most {UserQuestionRequest.MaxOptions} options; group the rest or ask in a second call."; return false; }
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var option in given)
            {
                var label = option?.Label?.Trim();
                if (string.IsNullOrEmpty(label)) { error = "Every option needs a label."; return false; }
                if (label.Length > UserQuestionRequest.MaxLabelLength) { error = $"Option labels must be at most {UserQuestionRequest.MaxLabelLength} characters; use description for detail."; return false; }
                if (!seen.Add(label)) { error = $"Option labels must be distinct ('{label}' appears twice)."; return false; }
                var description = string.IsNullOrWhiteSpace(option!.Description) ? null : option.Description.Trim();
                if (description is { Length: > UserQuestionRequest.MaxDescriptionLength }) { error = $"Option descriptions must be at most {UserQuestionRequest.MaxDescriptionLength} characters."; return false; }
                options.Add(new UserQuestionOption(label, description));
            }
        }
        var allowFreeText = input.AllowFreeText ?? true;
        if (options.Count == 0 && !allowFreeText) { error = "A question with no options must allow free text."; return false; }
        request = new UserQuestionRequest(conversationId, question, options, input.AllowMultiple == true && options.Count > 1, allowFreeText, context);
        return true;
    }
}

/// <summary>Publishes <c>user.ask</c> for engine lanes; the owner is the current turn's conversation.</summary>
public sealed class UserQuestionToolRegistration(IServiceProvider services) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        Register(services);
        return Task.CompletedTask;
    }

    /// <summary>For hosts that do not run hosted services. Safe to call repeatedly.</summary>
    public static void Register(IServiceProvider services)
    {
        var broker = services.GetRequiredService<IUserQuestionBroker>();
        var turns = services.GetRequiredService<ICurrentTurnContextAccessor>();
        services.GetRequiredService<ICapabilityCatalog>().Register(UserQuestionTools.Create(broker, () => turns.Current?.ConversationId));
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public static class UserQuestionServiceCollectionExtensions
{
    /// <summary>
    /// Opt-in <c>user.ask</c>: the tool registration for engine lanes (native lanes reach it as <c>user_ask</c> through
    /// the host tool bridge) and a TryAdd default <see cref="IUserQuestionBroker"/> that answers "no one can answer"
    /// (register the host's broker first). Deliberately not part of <see cref="CodingToolRegistrar.CodingToolIds"/>, so a
    /// host's admission policy is unchanged. The host adds <see cref="UserQuestionPrompts.Guidance"/> to its instructions.
    /// </summary>
    public static IServiceCollection AddSwarmCoreUserQuestions(this IServiceCollection services)
    {
        services.TryAddSingleton<IUserQuestionBroker>(NoUserQuestionBroker.Instance);
        if (!services.Any(d => d.ImplementationType == typeof(UserQuestionToolRegistration)))
            services.AddHostedService<UserQuestionToolRegistration>();
        return services;
    }
}
