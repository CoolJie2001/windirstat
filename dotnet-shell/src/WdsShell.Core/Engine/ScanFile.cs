using System.IO;
using WdsShell.Core.Models;

namespace WdsShell.Core.Engine;

/// <summary>
/// A file emitted by a scan. It is intentionally separate from DiskNode so
/// consumers can process the stream without depending on the UI tree.
/// </summary>
public sealed record ScanFile(
    string FullPath,
    string Name,
    long LogicalSize,
    long PhysicalSize,
    FileAttributes Attributes,
    DateTime? LastWriteTimeUtc,
    DiskNode Node);
