namespace Mrm.Infrastructure.Conflicts;

/// <summary>A single conflict item inside the conflict envelope.</summary>
public record ConflictItem(
    string Type,
    string Severity,   // "hard" | "soft"
    string Detail
);

/// <summary>Standard conflict envelope returned by all conflict endpoints.</summary>
public record ConflictEnvelope(
    bool Blocked,
    IReadOnlyList<ConflictItem> Conflicts
)
{
    public static ConflictEnvelope None() =>
        new(false, []);

    public static ConflictEnvelope Hard(params ConflictItem[] items) =>
        new(true, items);

    public static ConflictEnvelope Soft(params ConflictItem[] items) =>
        new(false, items);
}
