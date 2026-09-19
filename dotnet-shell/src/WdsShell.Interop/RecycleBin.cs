using System.Runtime.InteropServices;

namespace WdsShell.Interop;

public readonly record struct RecycleResult(string Path, bool Succeeded, string? Error);

/// <summary>Windows Shell recycle-bin operation. It never permanently deletes files.</summary>
public static partial class RecycleBin
{
    private const uint FoDelete = 0x0003;
    private const ushort FofSilent = 0x0004;
    private const ushort FofNoConfirmation = 0x0010;
    private const ushort FofAllowUndo = 0x0040;
    private const ushort FofNoErrorUi = 0x0400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOpStruct
    {
        public nint Hwnd;
        public uint WFunc;
        public nint PFrom;
        public nint PTo;
        public ushort FFlags;
        public int AnyOperationsAborted;
        public nint NameMappings;
        public nint ProgressTitle;
    }

    [LibraryImport("shell32.dll", EntryPoint = "SHFileOperationW")]
    private static partial int SHFileOperation(ref ShFileOpStruct operation);

    public static RecycleResult Move(string path)
    {
        if (!OperatingSystem.IsWindows())
            return new(path, false, "回收站操作仅支持 Windows");

        if (!File.Exists(path))
            return new(path, false, "文件已不存在");

        nint from = 0;
        try
        {
            var doubleNullTerminatedPath = path + '\0' + '\0';
            from = Marshal.StringToCoTaskMemUni(doubleNullTerminatedPath);
            var operation = new ShFileOpStruct
            {
                WFunc = FoDelete,
                PFrom = from,
                FFlags = FofAllowUndo | FofNoConfirmation | FofNoErrorUi | FofSilent,
            };

            var error = SHFileOperation(ref operation);
            return error == 0
                ? new(path, true, null)
                : new(path, false, $"Windows Shell 错误码 {error}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DllNotFoundException)
        {
            return new(path, false, ex.Message);
        }
        finally
        {
            if (from != 0) Marshal.FreeCoTaskMem(from);
        }
    }
}
