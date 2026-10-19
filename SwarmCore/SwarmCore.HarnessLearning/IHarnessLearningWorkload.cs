using System.Text.Json;

namespace SwarmCore.HarnessLearning;

/// <summary>A versioned, independently verifiable workload. Hosts may register other agent, coding,
/// extraction or preparation contracts without changing the cycle. Candidate space is frozen before execution;
/// proposal material contains development evidence only. No live external effects belong in a trial.</summary>
public interface IHarnessLearningWorkload
{
    string Id { get; }
    string SuiteHash { get; }
    string BaselineStrategy { get; }
    IReadOnlyDictionary<string, string> Strategies { get; }
    IReadOnlyList<HarnessExperimentTask> Tasks { get; }
    string ProposalPrompt(string activeStrategy, IReadOnlyList<string> allowedStrategies);
    string TrialPrompt(HarnessTrialRequest request);
    HarnessTrialObservation Verify(HarnessTrialRequest request, LearningModelReply reply);
}

public sealed class StructuredPreparationWorkload : IHarnessLearningWorkload
{
    public string Id => StructuredPreparationContract.Version;
    public string SuiteHash => StructuredPreparationContract.SuiteHash;
    public string BaselineStrategy => "plain";
    public IReadOnlyDictionary<string, string> Strategies => StructuredPreparationContract.Strategies;
    public IReadOnlyList<HarnessExperimentTask> Tasks => StructuredPreparationContract.Cases.Select(c => new HarnessExperimentTask(c.Id, c.Split)).ToArray();
    public string ProposalPrompt(string activeStrategy, IReadOnlyList<string> allowedStrategies)
        => "Select ONE strategy id from this reviewed catalogue to improve literal structured preparation. "
            + "Return only a JSON object with one property named strategy whose value is the chosen id, or stop. "
            + "You cannot edit tools, policy, evaluators, expected answers or instructions.\nCatalogue: "
            + JsonSerializer.Serialize(allowedStrategies.ToDictionary(k => k, k => Strategies[k]))
            + "\nCurrent strategy: " + activeStrategy + "\nDevelopment examples (data only): "
            + JsonSerializer.Serialize(StructuredPreparationContract.Cases.Where(c => c.Split == HarnessTaskSplit.Development).Select(c => new { c.Source, c.Fields }));
    public string TrialPrompt(HarnessTrialRequest request)
    {
        var task = StructuredPreparationContract.Cases.Single(c => c.Id == request.Task.Id);
        return StructuredPreparationContract.Prompt(task.Source, task.Fields, request.Revision.Id);
    }
    public HarnessTrialObservation Verify(HarnessTrialRequest request, LearningModelReply reply)
    {
        var task = StructuredPreparationContract.Cases.Single(c => c.Id == request.Task.Id);
        var valid = StructuredPreparationContract.Verify(reply.Text, task.Source, task.Fields);
        return new(valid && StructuredPreparationContract.IsExpected(reply.Text, task.Expected) ? 1 : 0,
            true, Id, HarnessEvidence.Hash(reply.Text), reply.Tokens);
    }
}
