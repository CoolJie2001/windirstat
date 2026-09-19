using WdsShell.Core.Engine;

namespace WdsShell.Core.Cleanup;

/// <summary>
/// A cleanup classifier evaluated against every file emitted by the active
/// scan. Rules must describe a file characteristic, not a hard-coded root.
/// </summary>
public sealed class CleanupRule
{
    public required string Id { get; init; }
    public required string Category { get; init; }
    public required string Description { get; init; }
    public required bool DefaultSelected { get; init; }
    public required Func<ScanFile, bool> IsMatch { get; init; }
}
