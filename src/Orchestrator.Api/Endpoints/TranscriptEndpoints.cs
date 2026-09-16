using Orchestrator.Adapters.Teams;
using Orchestrator.Api.Auth;
using Orchestrator.Application.Meetings;
using Orchestrator.Domain.Memory;

namespace Orchestrator.Api.Endpoints;

public sealed record TranscriptSegmentReviewDto(string Kind, double Confidence, string SupportingPassage);

public sealed record TranscriptProcessingResultDto(
    IReadOnlyList<Guid> CreatedTaskIds,
    IReadOnlyList<MemoryProposal> DecisionNotes,
    IReadOnlyList<TranscriptSegmentReviewDto> NeedsReview);

/// <summary>Administrator upload of approved meeting transcript exports.</summary>
public static class TranscriptEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/admin/transcripts")
            .RequireAuthorization(AdminOnly.PolicyName);
        group.MapPost("/", Upload);
    }

    private static async Task<IResult> Upload(
        TranscriptUploadRequest request, MeetingAssignmentService service, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.MeetingId) || request.Segments.Count == 0)
            return Results.BadRequest(new
            {
                message = "MeetingId and at least one segment are required.",
            });

        var result = await service.ProcessAsync(request.ToUpload(), ct);
        return Results.Ok(new TranscriptProcessingResultDto(
            result.CreatedTaskIds,
            result.DecisionNotes,
            result.NeedsReview
                .Select(c => new TranscriptSegmentReviewDto(
                    c.Kind.ToString(), c.Confidence, c.SupportingPassage))
                .ToList()));
    }
}
