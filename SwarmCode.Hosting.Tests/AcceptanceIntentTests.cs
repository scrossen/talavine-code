using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SwarmCore.Agent.Catalog;
using SwarmCore.Agent.Runtime;
using SwarmCore.Coding;
using SwarmCore.Coding.Permissions;
using SwarmCore.Coding.Routing;

namespace SwarmCode.Hosting.Tests;

public sealed class AcceptanceIntentTests
{
    private const string CatalogReplay = "Search the existing catalog service and replay it with input 13. "
        + "Then call catalog.add_service once with intentionally invalid code. "
        + "Report the failed compile receipt; do not fix or retry. No files or credentials.";

    [Theory]
    [InlineData(CatalogReplay)]
    [InlineData("Do not fix or retry. No files or credentials.")]
    [InlineData("Please do not edit any files.")]
    [InlineData("Don't change code or update tests.")]
    [InlineData("Do not edit, modify, or delete files.")]
    [InlineData("Do NOT, under any circumstances, fix the files.")]
    [InlineData("Please edit no files.")]
    [InlineData("Fix neither code nor tests.")]
    [InlineData("Never fix the code.")]
    [InlineData("I do not want you to change the repository.")]
    [InlineData("Avoid making code changes.")]
    [InlineData("Search the catalog without changing files.")]
    [InlineData("Please explain how to fix the code.")]
    [InlineData("Can you show me how to edit files?")]
    [InlineData("The next step would be to update tests.")]
    [InlineData("The report says to fix the code.")]
    [InlineData("Search for a service that can update files.")]
    [InlineData("Use catalog.add_service to add a service with this code.")]
    [InlineData("Add a catalog service with code that doubles its input.")]
    [InlineData("Create a service from this code.")]
    [InlineData("Add a service that returns code.")]
    [InlineData("Update the service; code is provided below.")]
    [InlineData("Update the saved service using the supplied code.")]
    [InlineData("Write an example of code that edits files.")]
    [InlineData("Write sample code in your answer.")]
    [InlineData("Write a code example.")]
    [InlineData("Write a report about the code.")]
    [InlineData("Add an explanation of the code to your answer.")]
    [InlineData("Return this text: \"Fix the code and update tests.\"")]
    [InlineData("'Edit files and fix bugs.'")]
    [InlineData("`Edit files and fix bugs.`")]
    [InlineData("Example:\n```text\nFix the code.\nUpdate tests.\n```\nReport what it means.")]
    [InlineData("Example:\n~~~text\nEdit the code.\n~~~")]
    [InlineData("Quoted request:\n> Fix the code.\n> Update the tests.\nExplain it.")]
    [InlineData("Call the tool with {\"instructions\": \"Edit the files. Update tests.\"}.")]
    public void Supporting_text_and_non_edit_requests_do_not_require_workspace_changes(string message)
        => Assert.False(Apply(message).RequireWorkspaceChange);

    [Theory]
    [InlineData("Can you implement those changes for me?")]
    [InlineData("Build this app in the current project folder.\nApp name: Calculator")]
    [InlineData("Create an app to track my garden.")]
    [InlineData("Can you build a calculator app?")]
    [InlineData("Please fix the failing tests.")]
    [InlineData("Make code changes in the app.")]
    [InlineData("Make changes to the code.")]
    [InlineData("Fix that bug.")]
    [InlineData("Update the project code.")]
    [InlineData("Don't forget to fix the code.")]
    [InlineData("Please go ahead and edit the file.")]
    [InlineData("Edit the file.")]
    [InlineData("Modify the code.")]
    [InlineData("Refactor the repository.")]
    [InlineData("Apply the changes.")]
    [InlineData("Create a file.")]
    [InlineData("Create a sample file.")]
    [InlineData("Update the instructions file.")]
    [InlineData("Do not edit code; do update the tests.")]
    [InlineData("Add regression tests.")]
    [InlineData("Write the code.")]
    [InlineData("Remove the unused code.")]
    [InlineData("Delete the obsolete file.")]
    [InlineData("Update the tests.")]
    [InlineData("Please edit `src/Parser.cs`.")]
    [InlineData("Change \"src/Parser.cs\" to handle empty input.")]
    [InlineData("Update README.md.")]
    [InlineData("I want you to fix the bug.")]
    [InlineData("Could you please modify the project?")]
    [InlineData("Do not touch credentials. Fix the code.")]
    [InlineData("Don't edit application code; add regression tests.")]
    [InlineData("Do not change the app, but update the tests.")]
    [InlineData("Do not retry; instead, fix the code.")]
    [InlineData("No credentials or files outside the repository. Please edit the file.")]
    [InlineData("Explain the bug first. Then fix the code.")]
    [InlineData("Example: `Do not edit files`. Please fix the bug.")]
    [InlineData("- Do not change credentials.\n- Fix the code.\n- Add tests.")]
    public void Direct_positive_edits_require_a_mutation_receipt(string message)
        => Assert.True(Apply(message).RequireWorkspaceChange);

    [Theory]
    [InlineData(CatalogReplay, "Fix the code.", false)]
    [InlineData("Fix the code.", CatalogReplay, true)]
    public void Intent_uses_the_authenticated_request_and_preserves_permission_authority(
        string original, string supportingMessage, bool expected)
    {
        var permissions = CodingPermissionPolicy.From(CodingPermissionMode.Ask,
            new CodingPermissionRules { Deny = ["fs.write_file"] });
        var request = SwarmCodeTurnPolicy.Apply(new CodingTurnRequest
        {
            ConversationId = "intent", UserMessage = supportingMessage,
            AuthenticatedOriginalUserMessage = original,
        }, permissions, "acceptance-test");

        Assert.Equal(expected, request.RequireWorkspaceChange);
        Assert.Same(permissions, request.Permissions);
        Assert.True(request.CommunicationScope!.UserInitiated);
        Assert.Equal("acceptance-test", request.CommunicationScope.Authority);
        Assert.Equal(CodingPermissionVerdict.Deny, request.Permissions.Decide("fs.write_file", ToolSideEffect.WritesLocal).Verdict);
        Assert.Equal(CodingPermissionVerdict.Ask, request.Permissions.Decide("code.edit_file", ToolSideEffect.WritesLocal).Verdict);
        Assert.NotNull(request.Permissions.NativeLaneBlock);
        Assert.Equal(original, request.AuthenticatedOriginalUserMessage);
    }

    [Theory]
    [InlineData(CatalogReplay, false, 1, false)]
    [InlineData("Do not retry the tool. Fix the code.", false, 2, true)]
    [InlineData("Do not retry the tool. Fix the code.", true, 1, false)]
    public async Task Acceptance_repairs_only_a_requested_edit_without_a_successful_mutation_receipt(
        string message, bool mutationReceipt, int attempts, bool validationError)
    {
        var runner = new ReceiptRunner(mutationReceipt);
        var services = new ServiceCollection();
        services.AddSingleton<IAgentTurnRunner>(runner);
        services.AddSingleton<ICodingLaneSource>(new OptionsCodingLaneSource(Options.Create(new CodingLaneOptions
        {
            Lanes = [new("test", "OpenRouter", "test-model")],
        })));
        using var provider = services.BuildServiceProvider();
        var orchestrator = new CodingTurnOrchestrator(provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new CodingOptions()), NullLogger<CodingTurnOrchestrator>.Instance);
        var events = new List<TurnEvent>();
        await foreach (var e in orchestrator.RunAsync(Apply(message) with
        {
            Workspace = new CodingWorkspace { RepositoryRoot = AppContext.BaseDirectory },
            SelectionPinned = true, LaneName = "test",
        })) events.Add(e);

        Assert.True(attempts == runner.Attempts, $"Expected {attempts} attempts, got {runner.Attempts}: {string.Join("; ", events.OfType<TurnErrored>().Select(e => e.Message))}");
        Assert.Equal(validationError, events.OfType<TurnErrored>().Any(e => e.ErrorKind == TurnErrorKind.CompletionValidation));
        Assert.Equal(validationError, events.OfType<CodingLaneNotice>().Any(e => e.Message.Contains("did not change repository files")));
        Assert.Contains(events, e => e is ToolCallCompleted { ToolName: "catalog.add_service", IsError: true });
        if (!validationError)
        {
            Assert.DoesNotContain(events, e => e is TurnErrored);
            Assert.Single(events.OfType<CodingAcceptanceEvaluated>());
        }
    }

    private static CodingTurnRequest Apply(string message) => SwarmCodeTurnPolicy.Apply(
        new CodingTurnRequest { ConversationId = "intent", UserMessage = message },
        CodingPermissionPolicy.AutoNoRules, "acceptance-test");

    private sealed class ReceiptRunner(bool mutationReceipt) : IAgentTurnRunner
    {
        public int Attempts { get; private set; }
        public async IAsyncEnumerable<TurnEvent> RunAsync(AgentTurnRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            Attempts++;
            await Task.Yield();
            yield return new ToolCallCompleted("t", "search", "catalog.search", "existing service", false, TimeSpan.Zero);
            yield return new ToolCallCompleted("t", "replay", "services.call", "26", false, TimeSpan.Zero);
            yield return new ToolCallCompleted("t", "invalid", "catalog.add_service", "compile rejected", true, TimeSpan.Zero);
            if (mutationReceipt)
                yield return new ToolCallCompleted("t", "edit", "code.edit_file", "edited", false, TimeSpan.Zero);
            yield return new TurnCompleted("t", "Input 13 returned 26. The intentional compile was rejected.", TimeSpan.Zero);
        }
    }
}
