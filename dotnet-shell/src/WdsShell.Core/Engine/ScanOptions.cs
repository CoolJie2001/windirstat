namespace WdsShell.Core.Engine;

/// <summary>
/// 扫描选项。字段与上游"高级"设置页 (CPageAdvanced) 的扫描排除项一一对应，
/// 默认值照抄 Options.h:174-182、286：联接与符号链接默认不跟踪，隐藏/受保护默认不排除。
/// </summary>
public sealed class ScanOptions
{
    /// <summary>对应上游 ExcludeHiddenFile：跳过带 Hidden 属性的文件。</summary>
    public bool ExcludeHiddenFile { get; init; }

    /// <summary>对应上游 ExcludeProtectedFile：跳过带 System 属性的文件（"受保护的操作系统文件"）。</summary>
    public bool ExcludeProtectedFile { get; init; }

    /// <summary>对应上游 ExcludeJunctions / ExcludeSymbolicLinksDirectory：为真时不进入目录联接与符号链接。</summary>
    public bool ExcludeLinkedDirectories { get; init; } = true;

    /// <summary>对应上游 ScanningThreads（1..16，默认 4）。</summary>
    public int WorkerThreads { get; init; } = 4;

    /// <summary>是否在卷根追加 &lt;Free Space&gt; 伪节点（上游始终追加，此处可关）。</summary>
    public bool ShowFreeSpace { get; init; } = true;
}
