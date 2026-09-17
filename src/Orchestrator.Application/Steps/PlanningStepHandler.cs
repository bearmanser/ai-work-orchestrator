using Orchestrator.Application.Agents;
using Orchestrator.Application.Context;
using Orchestrator.Application.Humans;
using Orchestrator.Application.Identity;
using Orchestrator.Application.Plans;
using Orchestrator.Application.Ports;
using Orchestrator.Domain.Authorization;
using Orchestrator.Domain.Identity;
using Orchestrator.Domain.Plans;
using Orchestrator.Domain.Tasks;

namespace Orchestrator.Application.Steps;

/// <summary>Configuration for the planning step.</summary>
public sealed class PlanningOptions
{
    public string FallbackChannelId { get; set; } = "C-plans";
}

/// <summary>
/// Assembles access-filtered context, asks an agent to draft a plan, and either
/// asks a blocking question or posts the plan for confirmation. A plan naming a
/// repository outside the task's envelope is never posted: it becomes a
/// blocking question instead, so nobody can confirm into unauthorized scope.
/// </summary>
public sealed class PlanningStepHandler(
    IPlanningScopeStore scope,
    ContextAssembler assembler,
    AgentRouter router,
    HumanQuestionService questions,
    PlanConfirmationService confirmations,
    OwnershipDirectory ownership,
    IPersonStore people,
    PlanningOptions options) : IStepHandler
{
    public string Capability => "planning";

    public async Task<StepOutcome> HandleAsync(StepContext context, CancellationToken ct)
    {
        var task = await scope.GetTaskAsync(context.TaskId, ct);
        var envelope = await scope.GetEnvelopeAsync(context.TaskId, ct)
            ?? throw new InvalidOperationException($"Task {context.TaskId} has no authorization envelope.");
        var ledger = await scope.GetLedgerAsync(context.TaskId, ct)
            ?? throw new InvalidOperationException($"Task {context.TaskId} has no budget ledger.");

        var budget = new ContextBudget(MaxCharacters: 100_000, MaxFilesPerRepository: 20, MaxVaultNotes: 5);
        var (bundle, snapshotId) = await assembler.AssembleAsync(task.Id, envelope, budget, ct);

        var request = new AgentRequest(
            task.Id,
            Guid.CreateVersion7().ToString("N"),
            Ports.AgentStage.Planning,
            new TrustedInstructions(
                task.Objective,
                ["Proposes repository changes with baselines", "Lists tests and risks"],
                ["Output a single ```json fenced plan proposal", "Do not implement anything"],
                ["Merging or deploying"]),
            bundle,
            WorkspaceFor(envelope),
            new ProviderPolicy(envelope.AllowedProviders, FallbackAllowed: false),
            envelope.Budget,
            envelope.Id.ToString());

        var result = await router.ExecuteAsync(request, ct);
        if (result.Status != AgentStatus.Succeeded)
        {
            return new StepOutcome(null, null,
                $"Agent planning failed: {result.FailureDetail ?? result.Status.ToString()}.");
        }

        PlanProposal proposal;
        try
        {
            proposal = PlanProposalParser.Parse(result.OutputText ?? string.Empty);
        }
        catch (AgentProtocolException ex)
        {
            return new StepOutcome(null, null, ex.Message);
        }

        var allowed = envelope.Repositories.Select(r => r.RepositoryId).ToHashSet();
        var outside = proposal.Content.RepositoryChanges
            .Where(c => !allowed.Contains(c.RepositoryId))
            .ToList();
        if (outside.Count > 0)
        {
            var names = string.Join(", ", outside.Select(c => c.RepositoryName));
            await questions.AskAsync(new AskRequest(
                task.Id, context.StepId, (await FallbackRecipientAsync(task, null, ct)).Id,
                $"The proposed plan targets {names}, which is outside this task's authorized scope. " +
                "Which repositories should it change instead?",
                ChannelFor(envelope), "orchestrator", envelope, ledger), ct);
            return new StepOutcome(TaskTrigger.BlockingQuestionRaised, null, null);
        }

        if (proposal.Questions.Count > 0)
        {
            var asked = proposal.Questions[0];
            var recipient = await FallbackRecipientAsync(task, asked.Domain, ct);
            await questions.AskAsync(new AskRequest(
                task.Id, context.StepId, recipient.Id, asked.Question,
                ChannelFor(envelope), "orchestrator", envelope, ledger), ct);
            return new StepOutcome(TaskTrigger.BlockingQuestionRaised, null, null);
        }

        var content = proposal.Content with { Budget = envelope.Budget };
        await confirmations.PostForConfirmationAsync(task.Id, content, ct, snapshotId);
        return new StepOutcome(TaskTrigger.PlanPosted, null, null);
    }

    private async Task<PersonIdentity> FallbackRecipientAsync(
        WorkTask task, string? domain, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(domain))
        {
            var owner = (await ownership.FindOwnersAsync(
                    OwnershipSubject.Domain, domain, ct))
                .FirstOrDefault(o => o.IsActive);
            if (owner is not null)
                return owner;
        }

        if (Guid.TryParse(task.RequesterId, out var requesterId))
        {
            var requester = await people.GetByIdAsync(requesterId, ct);
            if (requester is not null)
                return requester;
        }

        throw new InvalidOperationException($"Task {task.Id} has no one to ask: no domain owner and no resolvable requester.");
    }

    private string ChannelFor(AuthorizationEnvelope envelope) =>
        envelope.SlackChannels.FirstOrDefault() ?? options.FallbackChannelId;

    private static WorkspaceHandle WorkspaceFor(AuthorizationEnvelope envelope)
    {
        // Planning investigates through the assembled context bundle, not a checkout:
        // no work branch exists yet (implementation creates it in Task 38).
        // The handle names the first in-scope repository so the request stays well-formed.
        var first = envelope.Repositories.FirstOrDefault();
        if (first is null)
            return new WorkspaceHandle(string.Empty, string.Empty, string.Empty, string.Empty, string.Empty);

        return new WorkspaceHandle(
            $"mirror-{first.RepositoryId:N}", string.Empty,
            string.Empty, "main", "main");
    }
}
