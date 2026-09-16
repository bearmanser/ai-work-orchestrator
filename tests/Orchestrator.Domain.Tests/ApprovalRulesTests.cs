using Orchestrator.Domain.PullRequests;
using Shouldly;

namespace Orchestrator.Domain.Tests;

public class ApprovalRulesTests
{
    private const string Orchestrator = "U-orchestrator";
    private static readonly string[] Reviewers = ["U-magnus", "U-peer"];

    [Fact]
    public void Evaluate_ApprovedByConfiguredReviewerOnCurrentHead_IsApproved()
    {
        var outcome = ApprovalRules.Evaluate(
            Detail(head: "abc", reviewers: [Vote("U-magnus", 10, required: true)], threads: [], policiesPassing: true),
            Reviewers,
            Orchestrator);

        outcome.Approved.ShouldBeTrue();
    }

    [Fact]
    public void Evaluate_ApprovedOnlyByTheOrchestratorItself_IsNotApproved()
    {
        var outcome = ApprovalRules.Evaluate(
            Detail("abc", [Vote(Orchestrator, 10, true)], [], true),
            Reviewers,
            Orchestrator);

        outcome.Approved.ShouldBeFalse();
        outcome.Reason.ShouldContain("cannot approve its own");
    }

    [Fact]
    public void Evaluate_ApprovedByAnUnconfiguredPerson_IsNotApproved() =>
        ApprovalRules.Evaluate(
                Detail("abc", [Vote("U-stranger", 10, false)], [], true),
                Reviewers,
                Orchestrator)
            .Approved.ShouldBeFalse();

    [Fact]
    public void Evaluate_ApprovedWithSuggestions_CountsAsApproved() =>
        ApprovalRules.Evaluate(
                Detail("abc", [Vote("U-magnus", 5, true)], [], true),
                Reviewers,
                Orchestrator)
            .Approved.ShouldBeTrue();

    [Fact]
    public void Evaluate_AnyWaitingForAuthorVote_IsNotApproved() =>
        ApprovalRules.Evaluate(
                Detail("abc", [Vote("U-magnus", 10, true), Vote("U-peer", -5, true)], [], true),
                Reviewers,
                Orchestrator)
            .Approved.ShouldBeFalse();

    [Fact]
    public void Evaluate_AnyRejectVote_IsNotApproved() =>
        ApprovalRules.Evaluate(
            Detail("abc", [Vote("U-magnus", 10, true), Vote("U-peer", -10, true)], [], true),
            Reviewers,
            Orchestrator)
            .Approved.ShouldBeFalse();

    [Fact]
    public void Evaluate_ActiveCommentThread_IsNotApproved() =>
        ApprovalRules.Evaluate(
                Detail("abc", [Vote("U-magnus", 10, true)], [Thread(7, "active")], true),
                Reviewers,
                Orchestrator)
            .Approved.ShouldBeFalse();

    [Fact]
    public void Evaluate_ResolvedCommentThread_DoesNotBlock() =>
        ApprovalRules.Evaluate(
                Detail("abc", [Vote("U-magnus", 10, true)], [Thread(7, "fixed")], true),
                Reviewers,
                Orchestrator)
            .Approved.ShouldBeTrue();

    [Fact]
    public void Evaluate_FailingRequiredPolicy_IsNotApproved() =>
        ApprovalRules.Evaluate(
                Detail("abc", [Vote("U-magnus", 10, true)], [], policiesPassing: false),
                Reviewers,
                Orchestrator)
            .Approved.ShouldBeFalse();

    private static ApprovalVote Vote(string reviewerId, int vote, bool required) =>
        new(reviewerId, vote, required);

    private static ApprovalThread Thread(int threadId, string status) =>
        new(threadId, status);

    private static PullRequestApprovalDetail Detail(
        string head,
        IReadOnlyList<ApprovalVote> reviewers,
        IReadOnlyList<ApprovalThread> threads,
        bool policiesPassing)
    {
        _ = head;
        IReadOnlyList<ApprovalPolicy> policies = policiesPassing
            ? [new ApprovalPolicy("policy-1", "approved")]
            : [new ApprovalPolicy("policy-1", "rejected")];
        return new PullRequestApprovalDetail(reviewers, threads, policies);
    }
}
