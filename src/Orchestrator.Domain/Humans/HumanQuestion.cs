namespace Orchestrator.Domain.Humans;

public enum HumanQuestionStatus
{
    Open,
    ReminderSent,
    Escalated,
    Answered,
    Suspended,
}

/// <summary>
/// One question parked with a human while the task waits. The
/// <see cref="ChannelId"/>/<see cref="ThreadTs"/> pair is the durable Slack
/// mapping: answers are looked up by that pair, never by matching text.
/// Silence is never an answer — only <see cref="RecordAnswer"/> sets
/// <see cref="AnswerText"/>, and only the asked person's answer resumes work.
/// </summary>
public sealed class HumanQuestion
{
    private HumanQuestion() { } // EF

    public HumanQuestion(
        Guid taskId,
        Guid stepId,
        Guid recipientPersonId,
        string questionText,
        string channelId,
        string threadTs,
        string postedMessageTs,
        DateTimeOffset askedAt,
        DateTimeOffset remindAt,
        DateTimeOffset escalateAt,
        DateTimeOffset suspendAt)
    {
        if (taskId == Guid.Empty)
            throw new ArgumentException("Task id is required.", nameof(taskId));
        if (stepId == Guid.Empty)
            throw new ArgumentException("Step id is required.", nameof(stepId));
        if (recipientPersonId == Guid.Empty)
            throw new ArgumentException("Recipient person id is required.", nameof(recipientPersonId));
        if (string.IsNullOrWhiteSpace(questionText))
            throw new ArgumentException("Question text is required.", nameof(questionText));
        if (string.IsNullOrWhiteSpace(channelId))
            throw new ArgumentException("Channel id is required.", nameof(channelId));
        if (string.IsNullOrWhiteSpace(threadTs))
            throw new ArgumentException("Thread timestamp is required.", nameof(threadTs));
        if (string.IsNullOrWhiteSpace(postedMessageTs))
            throw new ArgumentException("Posted message timestamp is required.", nameof(postedMessageTs));

        Id = Guid.CreateVersion7();
        TaskId = taskId;
        StepId = stepId;
        RecipientPersonId = recipientPersonId;
        QuestionText = questionText;
        ChannelId = channelId;
        ThreadTs = threadTs;
        PostedMessageTs = postedMessageTs;
        AskedAt = askedAt.ToUniversalTime();
        RemindAt = remindAt.ToUniversalTime();
        EscalateAt = escalateAt.ToUniversalTime();
        SuspendAt = suspendAt.ToUniversalTime();
        Status = HumanQuestionStatus.Open;
    }

    public Guid Id { get; private set; }
    public Guid TaskId { get; private set; }
    public Guid StepId { get; private set; }
    public Guid RecipientPersonId { get; private set; }
    public string QuestionText { get; private set; } = null!;
    public string ChannelId { get; private set; } = null!;
    public string ThreadTs { get; private set; } = null!;
    public string PostedMessageTs { get; private set; } = null!;
    public DateTimeOffset AskedAt { get; private set; }
    public DateTimeOffset RemindAt { get; private set; }
    public DateTimeOffset EscalateAt { get; private set; }
    public DateTimeOffset SuspendAt { get; private set; }
    public string? AnswerText { get; private set; }
    public Guid? AnsweredByPersonId { get; private set; }
    public DateTimeOffset? AnsweredAt { get; private set; }
    public HumanQuestionStatus Status { get; private set; }

    /// <summary>
    /// Records an answer as evidence. Callers decide whether the answer resumes
    /// the task; this method only stores what was said, by whom, and when.
    /// </summary>
    public void RecordAnswer(string answerText, Guid? answeredByPersonId, DateTimeOffset answeredAt)
    {
        if (string.IsNullOrWhiteSpace(answerText))
            throw new ArgumentException("Answer text is required.", nameof(answerText));

        AnswerText = answerText;
        AnsweredByPersonId = answeredByPersonId;
        AnsweredAt = answeredAt.ToUniversalTime();
        Status = HumanQuestionStatus.Answered;
    }

    public void MarkReminderSent()
    {
        if (Status == HumanQuestionStatus.Open)
            Status = HumanQuestionStatus.ReminderSent;
    }

    public void MarkEscalated()
    {
        if (Status is HumanQuestionStatus.Open or HumanQuestionStatus.ReminderSent)
            Status = HumanQuestionStatus.Escalated;
    }

    public void MarkSuspended() => Status = HumanQuestionStatus.Suspended;
}
