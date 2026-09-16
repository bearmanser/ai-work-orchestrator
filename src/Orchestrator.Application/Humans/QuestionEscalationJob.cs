using Orchestrator.Application.Identity;
using Orchestrator.Application.Ports;
using Orchestrator.Domain.Humans;
using Orchestrator.Domain.Identity;
using Orchestrator.Domain.Tasks;

namespace Orchestrator.Application.Humans;

/// <summary>
/// Hourly escalation for unanswered questions: remind in the thread at +1
/// working day, notify the task owner at +3 days, suspend the task at +5 days.
/// Each stage fires at most once per question, and silence is never recorded
/// as an answer.
/// </summary>
public sealed class QuestionEscalationJob(
    IHumanQuestionStore questions,
    IQuestionTaskStore tasks,
    IPersonStore people,
    IChatGateway chat,
    TimeProvider clock)
{
    public async Task<int> RunAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var touched = 0;

        var open = await questions.ListOpenAsync(ct).ConfigureAwait(false);
        foreach (var question in open)
        {
            if (question.Status is HumanQuestionStatus.Answered or HumanQuestionStatus.Suspended)
                continue;

            var task = await tasks.GetAsync(question.TaskId, ct).ConfigureAwait(false);
            if (task is null || TaskStateMachine.IsTerminal(task.State))
                continue;

            if (question.Status == HumanQuestionStatus.Open && now >= question.RemindAt)
            {
                var recipient = await people.GetByIdAsync(question.RecipientPersonId, ct).ConfigureAwait(false);
                await chat.PostAsync(new ChatMessage(
                    question.ChannelId,
                    question.ThreadTs,
                    recipient?.SlackUserId,
                    $"Reminder: still waiting on {recipient?.DisplayName ?? "the asked person"} for an answer to '{question.QuestionText}'."), ct)
                    .ConfigureAwait(false);
                question.MarkReminderSent();
                await questions.SaveAsync(question, ct).ConfigureAwait(false);
                touched++;
            }

            if (question.Status is HumanQuestionStatus.Open or HumanQuestionStatus.ReminderSent
                && now >= question.EscalateAt)
            {
                var owner = await FindOwnerAsync(task.RequesterId, ct).ConfigureAwait(false);
                if (owner?.SlackUserId is not null)
                {
                    var recipient = await people.GetByIdAsync(question.RecipientPersonId, ct).ConfigureAwait(false);
                    await chat.PostAsync(new ChatMessage(
                        question.ChannelId,
                        question.ThreadTs,
                        owner.SlackUserId,
                        $"Escalating to the task owner ({owner.DisplayName}): " +
                        $"no answer yet to '{question.QuestionText}' " +
                        $"asked of {recipient?.DisplayName ?? "the asked person"}."), ct)
                        .ConfigureAwait(false);
                    question.MarkEscalated();
                    await questions.SaveAsync(question, ct).ConfigureAwait(false);
                    touched++;
                }
            }

            if (now >= question.SuspendAt
                && TaskStateMachine.TryTransition(task.State, TaskTrigger.QuestionTimedOut, out _))
            {
                task.Fire(TaskTrigger.QuestionTimedOut, now);
                await tasks.SaveAsync(task, ct).ConfigureAwait(false);
                question.MarkSuspended();
                await questions.SaveAsync(question, ct).ConfigureAwait(false);
                touched++;
            }
        }

        return touched;
    }

    private async Task<PersonIdentity?> FindOwnerAsync(string requesterId, CancellationToken ct)
    {
        if (!Guid.TryParse(requesterId, out var ownerId))
            return null;

        return await people.GetByIdAsync(ownerId, ct).ConfigureAwait(false);
    }
}
