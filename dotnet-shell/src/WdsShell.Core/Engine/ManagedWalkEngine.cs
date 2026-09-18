using System.Collections.Concurrent;
using WdsShell.Core.Models;

namespace WdsShell.Core.Engine;

/// <summary>
/// 托管递归扫描引擎（"占位引擎"）：
/// - 用途一：在 wdscore.dll（fork 自上游 C++ core，含 NTFS MFT 直读）就绪前，让 UI 全功能可跑；
/// - 用途二：作为跨平台回退（macOS/Linux 没有 NTFS MFT 可读）；
/// - 与 NativeWdsEngine 实现同一个 <see cref="IDiskScanEngine"/>，可在运行时按平台/权限互换。
/// 目录工作队列采用 work-stealing 模式，结构上对齐 C++ core 的 BlockingQueue&lt;CItem*&gt; 设计。
/// </summary>
public sealed class ManagedWalkEngine : IDiskScanEngine
{
    private readonly ConcurrentDictionary<string, ExtensionRecord> _extensionStats = new();
    private CancellationTokenSource? _cts;
    private CancellationToken _activeToken;
    private Task? _scanTask;
    private int _state;

    public DiskNode? Root { get; private set; }
    public string? ActivePath { get; private set; }
    public ScanState State => (ScanState)Volatile.Read(ref _state);
    public IReadOnlyDictionary<string, ExtensionRecord> ExtensionStats => _extensionStats;

    public event EventHandler<ScanState>? StateChanged;

    public Task StartScanAsync(string path, CancellationToken ct = default)
    {
        StopScan();

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _activeToken = _cts.Token;
        ActivePath = path;
        Root = new DiskNode(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar, NodeKind.Directory);
        _extensionStats.Clear();
        SetState(ScanState.Running);

        _scanTask = Task.Run(() => WalkAsync(_cts.Token), CancellationToken.None);
        return _scanTask;
    }

    public void StopScan()
    {
        _cts?.Cancel();
        try { _scanTask?.Wait(TimeSpan.FromSeconds(2)); } catch { /* 取消路径噪音 */ }
        if (State == ScanState.Running) SetState(ScanState.Stopped);
    }

    public void Dispose()
    {
        StopScan();
        _cts?.Dispose();
    }

    private void SetState(ScanState s)
    {
        Interlocked.Exchange(ref _state, (int)s);
        StateChanged?.Invoke(this, s);
    }

    private async Task WalkAsync(CancellationToken ct)
    {
        var root = Root!;
        var work = new BlockingCollection<DiskNode>();
        var pending = 1; // 队列中未完成的目录数，归零即扫描结束
        work.Add(root);

        void Worker()
        {
            foreach (var dir in work.GetConsumingEnumerable(ct))
            {
                try { ProcessDirectory(dir, work, ref pending); }
                catch (OperationCanceledException) { break; }
                finally
                {
                    if (Interlocked.Decrement(ref pending) == 0)
                        work.CompleteAdding();
                }
            }
        }

        var degree = Math.Max(2, Environment.ProcessorCount);
        var workers = Enumerable.Range(0, degree).Select(_ => Task.Run(Worker, CancellationToken.None));
        try { await Task.WhenAll(workers); }
        catch (OperationCanceledException) { /* 用户停止 */ }

        if (!ct.IsCancellationRequested)
            TryAppendFreeSpace(root);

        SetState(ct.IsCancellationRequested ? ScanState.Stopped : ScanState.Completed);
    }

    private void ProcessDirectory(DiskNode dir, BlockingCollection<DiskNode> work, ref int pending)
    {
        IEnumerable<string> entries;
        try
        {
            entries = Directory.EnumerateFileSystemEntries(dir.GetPath(), "*", new EnumerationOptions
            {
                RecurseSubdirectories = false,
                IgnoreInaccessible = true, // 无权限目录静默跳过（后续以 <Unknown> 伪节点呈现）
                AttributesToSkip = FileAttributes.Temporary,
            });
        }
        catch
        {
            return;
        }

        foreach (var entry in entries)
        {
            _activeToken.ThrowIfCancellationRequested();
            try
            {
                var isDir = Directory.Exists(entry);
                var name = Path.GetFileName(entry);
                if (string.IsNullOrEmpty(name)) continue;

                if (isDir)
                {
                    var reparse = (File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0;
                    var child = new DiskNode(name, NodeKind.Directory) { IsReparsePoint = reparse };
                    dir.Adopt(child);
                    child.RegisterDirectory();
                    if (!reparse) // junction/symlink 不跟踪，防环（与 C++ core 策略一致）
                    {
                        Interlocked.Increment(ref pending);
                        work.Add(child);
                    }
                }
                else
                {
                    long logical = 0;
                    try { logical = new FileInfo(entry).Length; } catch { }
                    // 脚手架估算：物理尺寸按 4KiB 簇向上取整。真实值由 native core 提供
                    // （NTFS 下应取 $DATA allocated length，并做 WOF/压缩修正）。
                    long physical = (logical + 4095) & ~4095L;
                    var file = new DiskNode(name, NodeKind.File);
                    dir.Adopt(file);
                    file.RegisterFile(physical, logical);
                    ExtensionStatReporter.Report(_extensionStats, name, physical);
                }
            }
            catch
            {
                // 单条目失败不影响整体扫描
            }
        }
    }

    private void TryAppendFreeSpace(DiskNode root)
    {
        try
        {
            var drive = DriveInfo.GetDrives().FirstOrDefault(d => d.IsReady &&
                root.GetPath().StartsWith(d.Name, StringComparison.OrdinalIgnoreCase));
            if (drive is null) return;
            var node = new DiskNode("<Free Space>", NodeKind.FreeSpace);
            root.Adopt(node);
            node.RegisterPseudoSize(drive.TotalFreeSpace);
        }
        catch { /* 非卷根路径时忽略 */ }
    }
}
