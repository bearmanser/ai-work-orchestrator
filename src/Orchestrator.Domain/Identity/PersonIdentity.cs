namespace Orchestrator.Domain.Identity;

/// <summary>
/// A mapped human identity. Resolution is by exact platform id only;
/// name-based guessing is explicitly out of scope.
/// </summary>
public sealed class PersonIdentity
{
    public PersonIdentity(
        Guid id,
        string displayName,
        string? slackUserId,
        string? azureDevOpsId,
        string? email,
        bool isActive)
    {
        if (id == Guid.Empty)
            throw new ArgumentException("Id is required.", nameof(id));
        if (string.IsNullOrWhiteSpace(displayName))
            throw new ArgumentException("Display name is required.", nameof(displayName));

        Id = id;
        DisplayName = displayName;
        SlackUserId = slackUserId;
        AzureDevOpsId = azureDevOpsId;
        Email = email;
        IsActive = isActive;
    }

    public Guid Id { get; private set; }

    public string DisplayName { get; private set; } = null!;

    public string? SlackUserId { get; private set; }

    public string? AzureDevOpsId { get; private set; }

    public string? Email { get; private set; }

    public bool IsActive { get; private set; }

    public void Deactivate() => IsActive = false;
}
