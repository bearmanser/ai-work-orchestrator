namespace Orchestrator.Domain.Operations;

/// <summary>One named operational kill-switch and its current value.</summary>
public sealed class OperationalFlag
{
    private OperationalFlag() { }

    public OperationalFlag(string key, bool value, DateTimeOffset updatedAt)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("Key is required.", nameof(key));

        Key = key;
        Value = value;
        UpdatedAt = updatedAt.ToUniversalTime();
    }

    public string Key { get; private set; } = null!;
    public bool Value { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public void Set(bool value, DateTimeOffset at)
    {
        Value = value;
        UpdatedAt = at.ToUniversalTime();
    }
}
