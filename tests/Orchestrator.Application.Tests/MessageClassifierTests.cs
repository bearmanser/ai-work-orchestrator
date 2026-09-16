using Orchestrator.Application.Slack;
using Shouldly;

namespace Orchestrator.Application.Tests;

public sealed class MessageClassifierTests
{
    private static readonly Guid TaskId = Guid.CreateVersion7();
    private static readonly Guid QuestionId = Guid.CreateVersion7();
    private static readonly Guid ApprovalId = Guid.CreateVersion7();

    private static readonly ConversationContext NoContext = new(null, null, null);

    [Fact]
    public void Classify_MentionInAChannelWithNoThreadTask_IsTaskRequest() =>
        MessageClassifier.Classify(Mention("@orchestrator add a GET endpoint for picked orders"), NoContext)
            .ShouldBe(MessageKind.TaskRequest);

    [Fact]
    public void Classify_ReplyInAThreadWithAPendingQuestion_IsQuestionAnswer() =>
        MessageClassifier.Classify(ThreadReply("include cancelled orders too"),
            new ConversationContext(TaskId, PendingQuestionId: QuestionId, PendingApprovalId: null))
            .ShouldBe(MessageKind.QuestionAnswer);

    [Fact]
    public void Classify_BlockActionOnAnApproval_IsApprovalResponse() =>
        MessageClassifier.Classify(BlockAction("confirm_plan"),
            new ConversationContext(TaskId, null, PendingApprovalId: ApprovalId))
            .ShouldBe(MessageKind.ApprovalResponse);

    [Fact]
    public void Classify_ReplyInATaskThreadWithNothingPending_IsClarification() =>
        MessageClassifier.Classify(ThreadReply("thanks"), new ConversationContext(TaskId, null, null))
            .ShouldBe(MessageKind.Clarification);

    [Fact]
    public void Classify_ChannelChatterWithoutAMention_IsUnrelated() =>
        MessageClassifier.Classify(PlainMessage("lunch at 12?"), NoContext).ShouldBe(MessageKind.Unrelated);

    [Fact]
    public void Classify_MentionThatIsAQuestionNotAnInstruction_IsUnrelated() =>
        // "do we have X" is not a work request; answering it is not starting a task.
        MessageClassifier.Classify(Mention("@orchestrator do we have an endpoint for picked orders?"), NoContext)
            .ShouldBe(MessageKind.Unrelated);

    [Fact]
    public void Classify_TextResemblingAPreviousQuestion_ButInAnotherThread_IsNotAnAnswer() =>
        MessageClassifier.Classify(PlainMessage("yes include cancelled orders"), NoContext)
            .ShouldNotBe(MessageKind.QuestionAnswer);

    internal static SlackInbound Mention(
        string text, string channel = "C1", string ts = "1737000000.000100", string userId = "U-translator") =>
        new(Guid.NewGuid().ToString(), SlackInboundKind.AppMention, channel, null, ts, userId, text);

    internal static SlackInbound ThreadReply(
        string text, string channel = "C1", string userId = "U-translator") =>
        new(Guid.NewGuid().ToString(), SlackInboundKind.ThreadReply, channel,
            "1737000000.000200", "1737000000.000300", userId, text);

    internal static SlackInbound BlockAction(
        string actionId, string channel = "C1", string userId = "U-translator") =>
        new(Guid.NewGuid().ToString(), SlackInboundKind.BlockAction, channel,
            "1737000000.000200", "1737000000.000400", userId, actionId);

    internal static SlackInbound PlainMessage(
        string text, string channel = "C1", string userId = "U-translator") =>
        new(Guid.NewGuid().ToString(), SlackInboundKind.DirectMessage, channel,
            null, "1737000000.000500", userId, text);
}
