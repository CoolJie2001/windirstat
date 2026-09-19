namespace WdsShell.Core.Cleanup;

/// <summary>
/// A deliberately small rule shape. The matcher is path-scoped and returns
/// false for everything outside its explicitly approved roots.
/// </summary>
public sealed class CleanupRule
{
    public required string Id { get; init; }
    public required string Category { get; init; }
    public required string Description { get; init; }
    public required int MinimumAgeDays { get; init; }
    public required bool DefaultSelected { get; init; }
    public required Func<string, bool> IsPathAllowed { get; init; }
}
