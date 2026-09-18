using System.Runtime.InteropServices;

namespace WdsShell.Interop;

/// <summary>
/// wds_core_api.h 的 C# 映射。仅本类允许出现 P/Invoke；
/// 上游/契约变更的波及面被锁死在这一个文件 + 下面的事件结构体。
/// </summary>
internal static unsafe partial class NativeMethods
{
    public const string LibraryName = "wdscore";
    public const uint WdsAbiVersion = 0x0001_0000;

    static NativeMethods()
    {
        // 每个程序集只允许注册一次；在静态构造里完成，避免重复注册抛异常。
        NativeLibrary.SetDllImportResolver(typeof(NativeMethods).Assembly, Resolver);
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void WdsEventCallback(nint context, WdsEvent* ev);

    [StructLayout(LayoutKind.Sequential)]
    public struct WdsOptions
    {
        public uint StructSize;
        public uint WorkerThreads;
        [MarshalAs(UnmanagedType.I1)] public bool PreferMft;
        [MarshalAs(UnmanagedType.I1)] public bool IncludeFreeSpace;
    }

    /// <summary>与 native/include/wds_core_api.h 的 WdsEvent 逐字段对齐（x64）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct WdsEvent
    {
        public uint Size;
        public uint Kind;
        public ulong RootToken;
        public ulong NodeToken;
        public ulong ParentToken;
        public nint Name;          // const wchar_t*
        public uint NameLength;
        public ulong LogicalSize;
        public ulong PhysicalSize;
        public uint Attributes;
        public uint State;
        public nint ExtName;       // const wchar_t*
        public uint ExtNameLength;
        public ulong ScannedBytes;
        public ulong FileCount;
    }

    public static class WdsEventKind
    {
        public const uint ScanStarted = 1;
        public const uint Dir = 2;
        public const uint File = 3;
        public const uint Progress = 4;
        public const uint Extension = 5;
        public const uint ScanState = 6;
    }

    [LibraryImport(LibraryName, EntryPoint = "wds_api_version")]
    public static partial uint ApiVersion();

    [LibraryImport(LibraryName, EntryPoint = "wds_create")]
    public static partial nint Create(WdsOptions* options);

    [LibraryImport(LibraryName, EntryPoint = "wds_destroy")]
    public static partial void Destroy(nint handle);

    [LibraryImport(LibraryName, EntryPoint = "wds_set_callback")]
    public static partial int SetCallback(nint handle, nint cb, nint context);

    [LibraryImport(LibraryName, EntryPoint = "wds_scan_start", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int ScanStart(nint handle, string pathSpec);

    [LibraryImport(LibraryName, EntryPoint = "wds_scan_stop")]
    public static partial int ScanStop(nint handle);

    [LibraryImport(LibraryName, EntryPoint = "wds_scan_abort")]
    public static partial int ScanAbort(nint handle);

    [LibraryImport(LibraryName, EntryPoint = "wds_scan_suspend")]
    public static partial int ScanSuspend(nint handle, [MarshalAs(UnmanagedType.I1)] bool suspend);

    [LibraryImport(LibraryName, EntryPoint = "wds_scan_is_running")]
    [return: MarshalAs(UnmanagedType.I1)]
    public static partial bool ScanIsRunning(nint handle);

    /// <summary>尝试加载 wdscore.dll 并校验 ABI 版本；失败返回 false 而不抛异常。</summary>
    public static bool TryLoad(out string failureReason)
    {
        failureReason = "";
        try
        {
            var version = ApiVersion();
            if (version >> 16 != WdsAbiVersion >> 16)
            {
                failureReason = $"wdscore.dll ABI 主版本不匹配 (dll: 0x{version:X8}, 期望: 0x{WdsAbiVersion:X8})";
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            failureReason = ex.Message;
            return false;
        }
    }

    private static nint Resolver(string libraryName, System.Reflection.Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName != LibraryName) return 0;
        // 优先 exe 侧 native/<arch> 子目录，其次默认探测
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "native", Environment.Is64BitProcess ? "x64" : "x86", "wdscore.dll"),
            Path.Combine(AppContext.BaseDirectory, "wdscore.dll"),
        };
        foreach (var candidate in candidates)
            if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out var handle))
                return handle;
        return NativeLibrary.TryLoad(LibraryName, assembly, searchPath, out var fallback) ? fallback : 0;
    }
}

