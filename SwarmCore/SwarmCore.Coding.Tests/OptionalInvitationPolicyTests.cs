using SwarmCore.Coding.Engagement;

namespace SwarmCore.Coding.Tests;

public sealed class OptionalInvitationPolicyTests
{
    private static readonly CodingCompletionEvidence VerifiedUserSuccess = new(
        Completed: true,
        Cancelled: false,
        HasError: false,
        AcceptancePassed: true,
        DirectUserRequest: true,
        PlanOnly: false,
        Temporary: false,
        Continuation: false,
        JournalSaved: true,
        SuccessfulWorkTool: false,
        AcceptanceFailed: false);

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void SavedDirectUserSuccessQualifiesWithEitherOrBothHostProofs(bool acceptancePassed, bool successfulWorkTool)
        => Assert.True(OptionalInvitationPolicy.Qualifies(VerifiedUserSuccess with
        {
            AcceptancePassed = acceptancePassed,
            SuccessfulWorkTool = successfulWorkTool,
        }));

    [Fact]
    public void DefaultNoCheckCompletionQualifiesAfterSuccessfulWorkToolReceipt()
        => Assert.True(OptionalInvitationPolicy.Qualifies(VerifiedUserSuccess with
        {
            AcceptancePassed = false,
            SuccessfulWorkTool = true,
            AcceptanceFailed = false,
        }));

    [Fact]
    public void CompletionWithoutEitherHostProofDoesNotQualify()
        => Assert.False(OptionalInvitationPolicy.Qualifies(VerifiedUserSuccess with { AcceptancePassed = false }));

    [Fact]
    public void MissingCompletionEvidenceDoesNotQualify()
        => Assert.False(OptionalInvitationPolicy.Qualifies(null));

    public static TheoryData<CodingCompletionEvidence> IneligibleCompletions => new()
    {
        VerifiedUserSuccess with { SuccessfulWorkTool = true, Completed = false },
        VerifiedUserSuccess with { SuccessfulWorkTool = true, Cancelled = true },
        VerifiedUserSuccess with { SuccessfulWorkTool = true, HasError = true },
        VerifiedUserSuccess with { SuccessfulWorkTool = true, DirectUserRequest = false },
        VerifiedUserSuccess with { SuccessfulWorkTool = true, PlanOnly = true },
        VerifiedUserSuccess with { SuccessfulWorkTool = true, Temporary = true },
        VerifiedUserSuccess with { SuccessfulWorkTool = true, Continuation = true },
        VerifiedUserSuccess with { SuccessfulWorkTool = true, JournalSaved = false },
        VerifiedUserSuccess with { SuccessfulWorkTool = true, AcceptanceFailed = true },
    };

    [Theory]
    [MemberData(nameof(IneligibleCompletions))]
    public void EachDisqualifyingCompletionFactPreventsEligibility(CodingCompletionEvidence evidence)
        => Assert.False(OptionalInvitationPolicy.Qualifies(evidence));

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, true, true, false)]
    [InlineData(true, false, false, true)]
    [InlineData(true, false, true, false)]
    [InlineData(true, true, false, false)]
    [InlineData(true, true, true, false)]
    public void DisplayRequiresUsefulSuccessWithoutDismissalOrActiveWork(
        bool hasUsefulSuccess, bool dismissed, bool busy, bool expected)
        => Assert.Equal(expected, OptionalInvitationPolicy.ShouldShow(hasUsefulSuccess, dismissed, busy));
}
