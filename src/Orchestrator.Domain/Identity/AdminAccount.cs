namespace Orchestrator.Domain.Identity;

/// <summary>
/// An operator account provisioned directly against the database by the
/// provision-admin CLI. There is no registration endpoint - this is the only way an
/// account is created.
/// </summary>
public sealed class AdminAccount
{
    private AdminAccount() { }

    public AdminAccount(string loginName, string passwordHash, DateTimeOffset createdAt)
    {
        Id = Guid.CreateVersion7();
        LoginName = loginName;
        PasswordHash = passwordHash;
        IsActive = true;
        CreatedAt = createdAt.ToUniversalTime();
        PasswordChangedAt = createdAt.ToUniversalTime();
        LastLoginAt = null;
    }

    public Guid Id { get; private set; }

    /// <summary>Stored lowercase and trimmed; looked up case-insensitively.</summary>
    public string LoginName { get; private set; } = null!;

    public string PasswordHash { get; private set; } = null!;
    public bool IsActive { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? LastLoginAt { get; private set; }
    public DateTimeOffset PasswordChangedAt { get; private set; }

    public void RecordLogin(DateTimeOffset at) => LastLoginAt = at.ToUniversalTime();

    public void ChangePasswordHash(string passwordHash, DateTimeOffset at)
    {
        PasswordHash = passwordHash;
        PasswordChangedAt = at.ToUniversalTime();
    }

    public void Deactivate() => IsActive = false;
}
