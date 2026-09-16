using Orchestrator.Application.Identity;
using Orchestrator.Application.Slack;

namespace Orchestrator.Application.Steps;

/// <summary>
/// Bridges inbound Slack traffic into the task system. The outbox dispatcher
/// routes each <c>slack.event</c> message here: mentions from known people
/// become tasks, everything else is left for the question (Task 35) and
/// approval (Task 36) services, which extend this handler's branching.
/// This is deliberately not an <see cref="IStepHandler"/> — no step exists
/// yet at intake time; intake is what creates the first one.
/// </summary>
public sealed class SlackEventStepHandler(
    TaskIntakeService intake,
    IdentityResolver identities)
{
    public async Task<IntakeResult> HandleSlackEventAsync(SlackInbound inbound, CancellationToken ct)
    {
        var context = new ConversationContext(null, null, null);
        if (MessageClassifier.Classify(inbound, context) != MessageKind.TaskRequest)
            return new IntakeResult(null, string.Empty);

        var requester = await identities.ResolveBySlackUserAsync(inbound.UserId, ct);
        return await intake.IntakeAsync(inbound, requester, ct);
    }
}
