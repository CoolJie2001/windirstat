using System.Collections.Concurrent;

namespace WdsShell.Core.Engine;

public enum ScanState
{
    Idle,
    Running,
    Paused,
    Completed,
    Stopped,
    Failed,
}

/// <summary>扩展名统计记录。对应 C++ core 的 SExtensionRecord。</summary>
public sealed class ExtensionRecord
{
    private long _files;
    private long _bytes;

    public void Add(long bytes)
    {
        Interlocked.Increment(ref _files);
        Interlocked.Add(ref _bytes, bytes);
    }

    public long Files => Volatile.Read(ref _files);
    public long Bytes => Volatile.Read(ref _bytes);
}

/// <summary>
/// 扫描引擎抽象 —— 这是 UI 与"引擎"之间唯一的接缝。
/// 当前实现为 <see cref="ManagedWalkEngine"/>（纯 C# 递归枚举，跨平台，用于先行开发 UI）；
/// 未来 wdscore.dll（fork 自上游 C++ core，含 NTFS MFT 直读）落地后，
/// WdsShell.Interop.NativeWdsEngine 实现同一接口即可无缝替换，UI 层零改动。
/// </summary>
public interface IDiskScanEngine : IDisposable
{
    Models.DiskNode? Root { get; }
    ScanState State { get; }
    string? ActivePath { get; }

    /// <summary>扩展名 → 统计。扫描期间持续更新，UI 节流读取。</summary>
    IReadOnlyDictionary<string, ExtensionRecord> ExtensionStats { get; }

    /// <summary>状态变化（Completed/Failed 等里程碑事件）。可能来自后台线程。</summary>
    event EventHandler<ScanState>? StateChanged;

    Task StartScanAsync(string path, CancellationToken ct = default);
    void StopScan();
}

/// <summary>引擎共用工具：扩展名统计收集。</summary>
public static class ExtensionStatReporter
{
    public static void Report(ConcurrentDictionary<string, ExtensionRecord> stats, string fileName, long physicalSize)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        if (ext.Length == 0) ext = "(无扩展名)";
        stats.GetOrAdd(ext, static _ => new ExtensionRecord()).Add(physicalSize);
    }
}
