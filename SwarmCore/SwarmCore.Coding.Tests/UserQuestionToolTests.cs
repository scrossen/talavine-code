using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SwarmCore.Agent.Abstractions;
using SwarmCore.Agent.Catalog;
using SwarmCore.Agent.Runtime;
using SwarmCore.Coding.Interaction;
using Xunit;

namespace SwarmCore.Coding.Tests;

/// <summary>
/// <c>user.ask</c>: the agent asks the person a question mid-turn. Owner comes from the trusted turn, input is validated,
/// the host broker's answer is returned verbatim, the harness default says nobody can answer, registration is opt-in,
/// and a cancelled turn never leaves a question hanging.
/// </summary>
public class UserQuestionToolTests
{
    private sealed class RecordingBroker(Func<UserQuestionRequest, CancellationToken, Task<UserAnswer>> answer) : IUserQuestionBroker
    {
        public UserQuestionRequest? Last { get; private set; }

        public Task<UserAnswer> AskAsync(UserQuestionRequest request, CancellationToken ct)
        {
            Last = request;
            return answer(request, ct);
        }
    }

    private static async Task<UserAskResult> Ask(ToolDescriptor tool, UserAskInput input, CancellationToken ct = default)
        => (UserAskResult)(await tool.Invoke(input, ct))!;

    [Fact]
    public void Tool_is_read_only_cheap_and_owned_by_the_interaction_agent()
    {
        var tool = UserQuestionTools.Create(NoUserQuestionBroker.Instance, () => "conv");
        Assert.Equal(UserQuestionTools.AskToolId, tool.ToolId);
        Assert.Equal("user.ask", tool.ToolId);
        Assert.Equal(UserQuestionTools.OwnerAgentId, tool.OwnerAgentId);
        Assert.Equal(ToolSideEffect.ReadOnly, tool.SideEffect); // plan turns and the native bridge's plan subset may ask
        Assert.Null(tool.ResolveSideEffect);
        Assert.Equal(ToolCostClass.Cheap, tool.Cost);
        Assert.Contains("genuinely", tool.Description);
        Assert.Contains("answered=false", tool.Description);
        Assert.NotNull(tool.InputSchema);
    }

    [Fact]
    public async Task Refuses_without_an_owning_turn()
    {
        var broker = new RecordingBroker((_, _) => Task.FromResult(UserAnswer.From("yes", null)));
        var tool = UserQuestionTools.Create(broker, () => null);
        var result = await Ask(tool, new UserAskInput("Ship it?"));
        Assert.False(result.Answered);
        Assert.Contains("conversation turn", result.Message);
        Assert.Null(broker.Last);
    }

    [Fact]
    public async Task Validates_input_before_asking()
    {
        var broker = new RecordingBroker((_, _) => Task.FromResult(UserAnswer.From("yes", null)));
        var tool = UserQuestionTools.Create(broker, () => "conv");

        var empty = await Ask(tool, new UserAskInput("  "));
        Assert.False(empty.Answered);
        Assert.Contains("question is required", empty.Message);

        var tooMany = await Ask(tool, new UserAskInput("Pick", Enumerable.Range(1, 9).Select(i => new UserQuestionOption("o" + i)).ToList()));
        Assert.False(tooMany.Answered);
        Assert.Contains("At most 8 options", tooMany.Message);

        var tooLong = await Ask(tool, new UserAskInput(new string('q', UserQuestionRequest.MaxQuestionLength + 1)));
        Assert.False(tooLong.Answered);
        Assert.Contains("2000", tooLong.Message);

        var blankLabel = await Ask(tool, new UserAskInput("Pick", [new UserQuestionOption(" ")]));
        Assert.Contains("label", blankLabel.Message);

        var duplicate = await Ask(tool, new UserAskInput("Pick", [new UserQuestionOption("A"), new UserQuestionOption("a")]));
        Assert.Contains("distinct", duplicate.Message);

        var nothingToAnswer = await Ask(tool, new UserAskInput("Pick", AllowFreeText: false));
        Assert.Contains("allow free text", nothingToAnswer.Message);

        Assert.Null(broker.Last);
    }

    [Fact]
    public async Task Passes_a_normalized_request_to_the_broker_and_returns_its_answer()
    {
        var broker = new RecordingBroker((_, _) => Task.FromResult(UserAnswer.From("  prefer the second  ", ["B"])));
        var tool = UserQuestionTools.Create(broker, () => "conv-7");
        var result = await Ask(tool, new UserAskInput(" Which? ", [new UserQuestionOption(" A ", "first"), new UserQuestionOption("B", " second ")],
            AllowMultiple: true, Context: " some background "));

        Assert.True(result.Answered);
        Assert.Equal("prefer the second", result.Text);
        Assert.Equal(["B"], result.Selected.ToArray());
        Assert.Contains("chose one option and added a note", result.Message);

        var request = Assert.IsType<UserQuestionRequest>(broker.Last);
        Assert.Equal("conv-7", request.ConversationId); // the trusted owner, never model input
        Assert.Equal("Which?", request.Question);
        Assert.Equal("some background", request.Context);
        Assert.True(request.AllowMultiple);
        Assert.True(request.AllowFreeText);
        Assert.Collection(request.Options,
            o => { Assert.Equal("A", o.Label); Assert.Equal("first", o.Description); },
            o => { Assert.Equal("B", o.Label); Assert.Equal("second", o.Description); });
    }

    [Fact]
    public void AllowMultiple_needs_more_than_one_option()
    {
        Assert.True(UserQuestionTools.TryBuild("c", new UserAskInput("Q", [new UserQuestionOption("only")], AllowMultiple: true), out var request, out _));
        Assert.False(request!.AllowMultiple);
    }

    [Fact]
    public async Task Default_broker_says_nobody_can_answer_and_the_model_must_decide()
    {
        var tool = UserQuestionTools.Create(NoUserQuestionBroker.Instance, () => "conv");
        var result = await Ask(tool, new UserAskInput("Which colour?", [new UserQuestionOption("red"), new UserQuestionOption("blue")]));
        Assert.False(result.Answered);
        Assert.Null(result.Text);
        Assert.Empty(result.Selected);
        Assert.Equal(UserAnswer.NoOneMessage, result.Message);
        Assert.Contains("Decide yourself", result.Message);
        Assert.Contains("answered=false", UserQuestionPrompts.Guidance);
    }

    [Fact]
    public async Task A_broker_failure_becomes_an_answer_the_model_can_act_on()
    {
        var tool = UserQuestionTools.Create(new RecordingBroker((_, _) => throw new InvalidOperationException("ui gone")), () => "conv");
        var result = await Ask(tool, new UserAskInput("Q"));
        Assert.False(result.Answered);
        Assert.Contains("ui gone", result.Message);
        Assert.Contains("Decide yourself", result.Message);
    }

    [Fact]
    public async Task Cancelling_the_turn_cancels_the_wait()
    {
        using var cts = new CancellationTokenSource();
        var broker = new RecordingBroker(async (_, ct) => { await Task.Delay(Timeout.InfiniteTimeSpan, ct); return UserAnswer.Declined; });
        var tool = UserQuestionTools.Create(broker, () => "conv");
        var pending = Ask(tool, new UserAskInput("Q"), cts.Token);
        Assert.False(pending.IsCompleted);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(10)));

        var already = new CancellationTokenSource();
        already.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Ask(tool, new UserAskInput("Q"), already.Token));
    }

    [Fact]
    public void Registration_is_opt_in_and_defers_to_a_host_broker()
    {
        var plain = new ServiceCollection();
        plain.AddLogging();
        plain.AddSwarmCoreCodingHarness();
        Assert.DoesNotContain(plain, d => d.ImplementationType == typeof(UserQuestionToolRegistration));
        Assert.DoesNotContain(plain, d => d.ServiceType == typeof(IUserQuestionBroker));
        Assert.DoesNotContain(UserQuestionTools.AskToolId, CodingToolRegistrar.CodingToolIds); // Talavine admission unchanged

        var optIn = new ServiceCollection();
        optIn.AddLogging();
        optIn.AddSwarmCoreCodingHarness();
        optIn.AddSwarmCoreUserQuestions();
        optIn.AddSwarmCoreUserQuestions(); // idempotent
        Assert.Single(optIn, d => d.ImplementationType == typeof(UserQuestionToolRegistration));
        Assert.IsType<NoUserQuestionBroker>(Assert.Single(optIn, d => d.ServiceType == typeof(IUserQuestionBroker)).ImplementationInstance);

        var hostFirst = new ServiceCollection();
        var host = new RecordingBroker((_, _) => Task.FromResult(UserAnswer.Declined));
        hostFirst.AddSingleton<IUserQuestionBroker>(host);
        hostFirst.AddSwarmCoreUserQuestions();
        Assert.Same(host, Assert.Single(hostFirst, d => d.ServiceType == typeof(IUserQuestionBroker)).ImplementationInstance);
    }

    [Fact]
    public async Task Registration_publishes_the_tool_with_the_current_turn_as_owner()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var turns = new AsyncLocalCurrentTurnContextAccessor();
        services.AddSingleton<ICurrentTurnContextAccessor>(turns);
        services.AddSingleton<ICapabilityCatalog, InMemoryCapabilityCatalog>();
        var broker = new RecordingBroker((_, _) => Task.FromResult(UserAnswer.From(null, ["yes"])));
        services.AddSingleton<IUserQuestionBroker>(broker);
        services.AddSwarmCoreUserQuestions();
        using var provider = services.BuildServiceProvider();
        foreach (var hosted in provider.GetServices<IHostedService>().OfType<UserQuestionToolRegistration>()) await hosted.StartAsync(default);

        var tool = Assert.Single(provider.GetRequiredService<ICapabilityCatalog>().All(), t => t.ToolId == UserQuestionTools.AskToolId);
        var outside = await Ask(tool, new UserAskInput("Q"));
        Assert.False(outside.Answered);
        Assert.Null(broker.Last);

        turns.Current = new CurrentTurnContext("conv-turn", null, null);
        try
        {
            var inside = await Ask(tool, new UserAskInput("Q", [new UserQuestionOption("yes"), new UserQuestionOption("no")]));
            Assert.True(inside.Answered);
            Assert.Equal("conv-turn", broker.Last!.ConversationId);
        }
        finally { turns.Current = null; }
    }
}
