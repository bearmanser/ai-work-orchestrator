using Orchestrator.Application.Gateway;
using Orchestrator.Application.Identity;
using Orchestrator.Application.Ports;
using Orchestrator.Domain.Authorization;
using Orchestrator.Domain.Budgets;
using Orchestrator.Domain.Humans;
using Orchestrator.Domain.Tasks;

namespace Orchestrator.Application.Humans;

/// <summary>
/// Thrown when policy does not permit contacting the recipient. The chat
/// transport is never invoked in that case — nothing is posted.
/// </summary>
public sealed class PolicyDeniedException(string message) : Exception(message);

public sealed record AskRequest(
    Guid TaskId,
    Guid StepId,
    Guid RecipientPersonId,
    string QuestionText,
    string ChannelId,
    string Actor,
    AuthorizationEnvelope Envelope,
    BudgetLedger Budget);

public sealed record AnswerOutcome(bool Accepted, Guid? QuestionId, string Reason);

/// <summary>
/// The lease state of one step as far as the question loop is concerned.
/// Carried as a small DTO so asking never needs to mutate the step aggregate.
/// </summary>
public sealed record StepLeaseInfo(Guid StepId, Guid TaskId, string? LeaseOwner);

public interface IHumanQuestionStore
{
    Task AddAsync(HumanQuestion question, CancellationToken ct);

    Task<HumanQuestion?> GetAsync(Guid id, CancellationToken ct);

    Task<HumanQuestion?> FindByChannelThreadAsync(string channelId, string threadTs, CancellationToken ct);

    Task<IReadOnlyList<HumanQuestion>> ListOpenAsync(CancellationToken ct);

    Task SaveAsync(HumanQuestion question, CancellationToken ct);
}

public interface IQuestionTaskStore
{
    Task<WorkTask?> GetAsync(Guid taskId, CancellationToken ct);

    Task SaveAsync(WorkTask task, CancellationToken ct);
}

public interface IQuestionStepLeases
{
    Task<StepLeaseInfo?> GetAsync(Guid stepId, CancellationToken ct);

    Task ReleaseAsync(Guid stepId, CancellationToken ct);
}

/// <summary>
/// Working-day arithmetic with configured non-working days. Weekends are never
/// working days; a question asked Friday afternoon reminds Monday, not Saturday.
/// </summary>
public sealed class WorkingDayCalculator(IReadOnlySet<DateOnly> holidays)
{
    public bool IsWorkingDay(DateOnly date) =>
        date.DayOfWeek is not DayOfWeek.Saturday and not DayOfWeek.Sunday
        && !holidays.Contains(date);

    public DateTimeOffset AddWorkingDays(DateTimeOffset start, int workingDays)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(workingDays);

        var elapsed = 0;
        var added = 0;
        var date = DateOnly.FromDateTime(start.UtcDateTime);
        while (added < workingDays)
        {
            date = date.AddDays(1);
            elapsed++;
            if (IsWorkingDay(date))
                added++;
        }

        return start.AddDays(elapsed);
    }
}

/// <summary>
/// The durable human question loop: ask through the policy gateway, persist the
/// channel/thread mapping, and route answers back to the task. Contacting
/// someone outside the envelope is a policy denial, raised before anything is
/// posted. Answers are looked up by channel/thread pair, never by text.
/// </summary>
public sealed class HumanQuestionService(
    IHumanQuestionStore questions,
    IQuestionTaskStore tasks,
    IQuestionStepLeases steps,
    IPersonStore people,
    IToolGateway gateway,
    IChatGateway chat,
    IdentityResolver identity,
    IAuditWriter audit,
    TimeProvider clock,
    WorkingDayCalculator calendar)
{
    public async Task<HumanQuestion> AskAsync(AskRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.QuestionText))
            throw new ArgumentException("Question text is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.ChannelId))
            throw new ArgumentException("Channel id is required.", nameof(request));

        var person = await people.GetByIdAsync(request.RecipientPersonId, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Person {request.RecipientPersonId} was not found.");
        var task = await tasks.GetAsync(request.TaskId, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Task {request.TaskId} was not found.");

        var now = clock.GetUtcNow();
        var operationId = $"human-question-{Guid.CreateVersion7():N}";
        var call = new ToolCall(
            request.TaskId,
            request.StepId,
            request.Actor,
            ActionKind.ContactPerson,
            ResourceScope.Person(person.SlackUserId ?? person.Id.ToString()),
            operationId,
            operationId,
            $"Ask {person.DisplayName}: {request.QuestionText}",
            request.Envelope,
            request.Budget);

        var result = await gateway.InvokeAsync<PostedMessage>(
            call,
            innerCt => chat.PostAsync(
                new ChatMessage(request.ChannelId, null, person.SlackUserId, request.QuestionText),
                innerCt),
            ct).ConfigureAwait(false);

        if (!result.Executed || result.Value is null)
            throw new PolicyDeniedException($"Not contacting {person.DisplayName}: {DecisionReason(result.Decision)}.");

        var posted = result.Value;
        var question = new HumanQuestion(
            request.TaskId,
            request.StepId,
            person.Id,
            request.QuestionText,
            posted.ChannelId,
            posted.ThreadTs,
            posted.MessageTs,
            now,
            calendar.AddWorkingDays(now, 1),
            now.AddDays(3),
            now.AddDays(5));

        await questions.AddAsync(question, ct).ConfigureAwait(false);

        task.Fire(TaskTrigger.BlockingQuestionRaised, now);
        await tasks.SaveAsync(task, ct).ConfigureAwait(false);

        // The step lease is released: no agent session is held while a human is asked.
        await steps.ReleaseAsync(request.StepId, ct).ConfigureAwait(false);

        await audit.RecordAsync(new AuditEntry(
            "orchestrator",
            task.Id,
            "question.asked",
            $"Asked {person.DisplayName} in {posted.ChannelId}.",
            "Success",
            operationId,
            new Dictionary<string, string> { ["questionId"] = question.Id.ToString() }), ct).ConfigureAwait(false);

        return question;
    }

    public async Task<AnswerOutcome> RecordAnswerAsync(
        string channelId,
        string threadTs,
        string fromSlackUserId,
        string text,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var question = await questions
            .FindByChannelThreadAsync(channelId, threadTs, ct)
            .ConfigureAwait(false);
        if (question is null)
            return new AnswerOutcome(false, null, "No open question matches this thread; the reply was ignored.");

        if (question.Status is HumanQuestionStatus.Answered or HumanQuestionStatus.Suspended)
            return new AnswerOutcome(false, question.Id, "This question has already been answered.");

        var task = await tasks.GetAsync(question.TaskId, ct).ConfigureAwait(false);
        if (task is null)
            return new AnswerOutcome(false, question.Id, "The task for this question no longer exists; the reply was ignored.");

        if (string.IsNullOrWhiteSpace(text))
            return new AnswerOutcome(false, question.Id, "Empty replies are ignored; silence is never an answer.");

        var person = await identity.ResolveBySlackUserAsync(fromSlackUserId, ct).ConfigureAwait(false);

        if (TaskStateMachine.IsTerminal(task.State))
        {
            // Kept as evidence, never resumes.
            question.RecordAnswer(text, person?.Id, now);
            await questions.SaveAsync(question, ct).ConfigureAwait(false);
            await audit.RecordAsync(new AuditEntry(
                fromSlackUserId,
                task.Id,
                "question.answer.late",
                $"Answer arrived after the task became {task.State}; kept as evidence.",
                "Ignored",
                question.Id.ToString(),
                null), ct).ConfigureAwait(false);
            return new AnswerOutcome(
                false,
                question.Id,
                $"The task is {task.State}; the answer was kept as evidence without resuming.");
        }

        if (person is null || person.Id != question.RecipientPersonId)
        {
            await audit.RecordAsync(new AuditEntry(
                fromSlackUserId,
                task.Id,
                "question.answer.bystander",
                "Reply from someone who was not asked; kept in the audit trail only.",
                "Ignored",
                question.Id.ToString(),
                null), ct).ConfigureAwait(false);
            return new AnswerOutcome(
                false,
                question.Id,
                $"User '{fromSlackUserId}' was not asked this question; still waiting for the asked person.");
        }

        question.RecordAnswer(text, person.Id, now);
        if (TaskStateMachine.TryTransition(task.State, TaskTrigger.QuestionAnswered, out _))
        {
            task.Fire(TaskTrigger.QuestionAnswered, now);
            await tasks.SaveAsync(task, ct).ConfigureAwait(false);
        }

        await questions.SaveAsync(question, ct).ConfigureAwait(false);
        await audit.RecordAsync(new AuditEntry(
            fromSlackUserId,
            task.Id,
            "question.answered",
            $"Answer accepted from {person.DisplayName}.",
            "Success",
            question.Id.ToString(),
            null), ct).ConfigureAwait(false);

        return new AnswerOutcome(true, question.Id, "Answer accepted from the asked person; the task resumed planning.");
    }

    private static string DecisionReason(PolicyDecision decision) => decision switch
    {
        PolicyDecision.Permitted permitted => permitted.Reason,
        PolicyDecision.Denied denied => denied.Reason,
        PolicyDecision.ApprovalRequired approvalRequired => approvalRequired.Reason,
        _ => "Unknown policy decision.",
    };
}
