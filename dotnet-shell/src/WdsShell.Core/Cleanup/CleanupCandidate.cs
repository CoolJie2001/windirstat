using WdsShell.Core.Models;

namespace WdsShell.Core.Cleanup;

public sealed record CleanupCandidate(
    string RuleId,
    string Category,
    string Description,
    string FullPath,
    long Size,
    DateTime LastWriteTimeUtc,
    DiskNode Node,
    bool DefaultSelected);
