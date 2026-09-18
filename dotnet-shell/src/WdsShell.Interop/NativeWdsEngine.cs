using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using WdsShell.Core.Engine;
using WdsShell.Core.Models;

namespace WdsShell.Interop;

/// <summary>
/// wdscore.dll（上游 C++ core 裁剪产物）的 .NET 侧引擎。
/// 事件→DiskNode 的翻译逻辑已完整实现；只差 native 构建落地。
/// 引擎选择策略（App 启动时）：Windows + dll 可用 + 有权限 → 本引擎；否则回退 ManagedWalkEngine。
/// </summary>
public sealed unsafe class NativeWdsEngine : IDiskScanEngine
{
    private readonly ConcurrentDictionary<string, ExtensionRecord> _extensionStats = new();
    private readonly ConcurrentDictionary<ulong, DiskNode> _nodesByToken = new();
    private nint _handle;
    private GCHandle _callbackPin;
    private NativeMethods.WdsEventCallback? _callback;
    private int _state;

    public DiskNode? Root { get; private set; }
    public string? ActivePath { get; private set; }
    public ScanState State => (ScanState)Volatile.Read(ref _state);
    public IReadOnlyDictionary<string, ExtensionRecord> ExtensionStats => _extensionStats;
    public event EventHandler<ScanState>? StateChanged;

    /// <summary>native core 是否可用（dll 存在且 ABI 匹配）。App 据此决定引擎。</summary>
    public static bool IsAvailable(out string reason) => NativeMethods.TryLoad(out reason);

    public Task StartScanAsync(string path, CancellationToken ct = default)
    {
        if (!IsAvailable(out var reason))
            throw new PlatformNotSupportedException(
                $"wdscore.dll 尚未构建（native/ 目录，见 docs/UPSTREAM_SYNC.md）。当前应回退 ManagedWalkEngine。原因: {reason}");

        _callback ??= OnNativeEvent;
        _callbackPin = GCHandle.Alloc(_callback);

        var options = new NativeMethods.WdsOptions
        {
            StructSize = (uint)sizeof(NativeMethods.WdsOptions),
            WorkerThreads = 0,
            PreferMft = true,
            IncludeFreeSpace = true,
        };
        _handle = NativeMethods.Create(&options);
        if (_handle == 0)
            throw new InvalidOperationException("wds_create 失败：可能需要管理员权限或不受支持的 OS。");

        int hr = NativeMethods.SetCallback(_handle,
            Marshal.GetFunctionPointerForDelegate(_callback), nint.Zero);
        if (hr != 0) throw new InvalidOperationException($"wds_set_callback 失败: {hr}");

        ActivePath = path;
        _nodesByToken.Clear();
        _extensionStats.Clear();
        SetState(ScanState.Running);

        hr = NativeMethods.ScanStart(_handle, path);
        if (hr != 0) throw new InvalidOperationException($"wds_scan_start({path}) 失败: {hr}");

        return Task.CompletedTask; // 完成信号经由 StateChanged 事件推送
    }

    public void StopScan()
    {
        if (_handle != 0) NativeMethods.ScanStop(_handle);
    }

    public void Dispose()
    {
        if (_handle != 0)
        {
            NativeMethods.Destroy(_handle);
            _handle = 0;
        }
        if (_callbackPin.IsAllocated) _callbackPin.Free();
    }

    private void SetState(ScanState s)
    {
        Interlocked.Exchange(ref _state, (int)s);
        StateChanged?.Invoke(this, s);
    }

    // 回调可能来自任意 core worker 线程；只触碰并发安全结构，UI 由 shell 侧节流快照。
    [MonoPInvokeCallback(typeof(NativeMethods.WdsEventCallback))]
    private void OnNativeEvent(nint context, NativeMethods.WdsEvent* ev)
    {
        if (ev is null || ev->Size < sizeof(NativeMethods.WdsEvent)) return;
        switch (ev->Kind)
        {
            case NativeMethods.WdsEventKind.ScanStarted:
                Root = new DiskNode(ReadString(ev->Name, ev->NameLength), NodeKind.Directory);
                _nodesByToken[ev->RootToken] = Root;
                break;

            case NativeMethods.WdsEventKind.Dir:
                if (_nodesByToken.TryGetValue(ev->ParentToken, out var dirParent))
                {
                    var node = new DiskNode(ReadString(ev->Name, ev->NameLength), NodeKind.Directory)
                    {
                        IsReparsePoint = (ev->Attributes & 0x400 /* FILE_ATTRIBUTE_REPARSE_POINT */) != 0,
                    };
                    dirParent.Adopt(node);
                    node.RegisterDirectory();
                    _nodesByToken[ev->NodeToken] = node;
                }
                break;

            case NativeMethods.WdsEventKind.File:
                if (_nodesByToken.TryGetValue(ev->ParentToken, out var fileParent))
                {
                    var name = ReadString(ev->Name, ev->NameLength);
                    var node = new DiskNode(name, NodeKind.File);
                    fileParent.Adopt(node);
                    node.RegisterFile((long)ev->PhysicalSize, (long)ev->LogicalSize);
                    ExtensionStatReporter.Report(_extensionStats, name, (long)ev->PhysicalSize);
                }
                break;

            case NativeMethods.WdsEventKind.Extension:
                var ext = ReadString(ev->ExtName, ev->ExtNameLength);
                _extensionStats.GetOrAdd(ext, static _ => new ExtensionRecord()).Add((long)ev->PhysicalSize);
                break;

            case NativeMethods.WdsEventKind.ScanState:
                SetState((ScanState)ev->State);
                break;
        }
    }

    private static string ReadString(nint ptr, uint length)
        => ptr == 0 || length == 0
            ? string.Empty
            : Marshal.PtrToStringUni(ptr, (int)length) ?? string.Empty;
}

/// <summary>标记托管方法可被 native 侧作为函数指针回调。</summary>
[AttributeUsage(AttributeTargets.Method)]
file sealed class MonoPInvokeCallbackAttribute(Type delegateType) : Attribute
{
    public Type DelegateType { get; } = delegateType;
}
