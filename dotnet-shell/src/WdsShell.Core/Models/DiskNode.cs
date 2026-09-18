namespace WdsShell.Core.Models;

public enum NodeKind
{
    Directory,
    File,
    /// <summary>伪节点：卷剩余空间（对应 WinDirStat 的 &lt;Free Space&gt;）。</summary>
    FreeSpace,
    /// <summary>伪节点：无法统计的空间（对应 &lt;Unknown&gt;）。</summary>
    Unknown,
}

/// <summary>
/// 磁盘树节点。与 C++ core 的 CItem 角色对应，但为纯数据模型——
/// 不包含任何绘制逻辑（上游 CItem 继承 UI 控件基类是我们明确不复刻的耦合点）。
/// 写入协议：扫描线程调用 Adopt + Register*，UI 线程只做快照读取。
/// </summary>
public sealed class DiskNode
{
    private long _physicalSize;
    private long _logicalSize;
    private long _fileCount;
    private long _dirCount;
    private int _childCount;

    private readonly object _childrenGate = new();
    private readonly List<DiskNode> _children = [];

    public DiskNode(string name, NodeKind kind)
    {
        Name = name;
        Kind = kind;
        Extension = kind == NodeKind.File
            ? Path.GetExtension(name).ToLowerInvariant()
            : string.Empty;
    }

    public string Name { get; }
    public NodeKind Kind { get; }
    public DiskNode? Parent { get; private set; }
    public string Extension { get; }
    public bool IsDirectory => Kind is NodeKind.Directory or NodeKind.FreeSpace or NodeKind.Unknown;
    public bool IsReparsePoint { get; init; }

    public long PhysicalSize => Volatile.Read(ref _physicalSize);
    public long LogicalSize => Volatile.Read(ref _logicalSize);
    public long FileCount => Volatile.Read(ref _fileCount);
    public long DirCount => Volatile.Read(ref _dirCount);
    public int ChildCount => Volatile.Read(ref _childCount);

    /// <summary>纯登记：挂到父节点 children 下。尺寸统计走 Register* 系列方法。</summary>
    public void Adopt(DiskNode child)
    {
        child.Parent = this;
        lock (_childrenGate)
        {
            _children.Add(child);
            Interlocked.Increment(ref _childCount);
        }
    }

    /// <summary>在文件节点自身上调用（须先 Adopt）：自身与全部祖先累加尺寸，祖先累加文件计数。</summary>
    public void RegisterFile(long physical, long logical)
    {
        for (var p = this; p is not null; p = p.Parent)
        {
            Interlocked.Add(ref p._physicalSize, physical);
            Interlocked.Add(ref p._logicalSize, logical);
            if (!ReferenceEquals(p, this))
                Interlocked.Add(ref p._fileCount, 1);
        }
    }

    /// <summary>在目录节点自身上调用（须先 Adopt）：祖先累加目录计数。</summary>
    public void RegisterDirectory()
    {
        for (var p = Parent; p is not null; p = p.Parent)
            Interlocked.Add(ref p._dirCount, 1);
    }

    /// <summary>在伪节点（FreeSpace/Unknown）自身上调用（须先 Adopt）：沿链累加尺寸。</summary>
    public void RegisterPseudoSize(long bytes)
    {
        for (var p = this; p is not null; p = p.Parent)
        {
            Interlocked.Add(ref p._physicalSize, bytes);
            Interlocked.Add(ref p._logicalSize, bytes);
        }
    }

    /// <summary>UI 快照：返回按物理尺寸降序排列的子节点数组。锁粒度为单节点，扫描期可安全调用。</summary>
    public DiskNode[] SnapshotChildren()
    {
        lock (_childrenGate)
        {
            var copy = _children.ToArray();
            Array.Sort(copy, static (a, b) => b.PhysicalSize.CompareTo(a.PhysicalSize));
            return copy;
        }
    }

    /// <summary>完整路径（自根拼接；根节点 Name 即绝对路径）。</summary>
    public string GetPath()
    {
        var parts = new List<string>();
        for (var p = this; p is not null; p = p.Parent)
            parts.Add(p.Name);
        parts.Reverse();
        var sb = new System.Text.StringBuilder();
        foreach (var part in parts)
        {
            if (sb.Length > 0 && sb[^1] != Path.DirectorySeparatorChar && part.StartsWith(Path.DirectorySeparatorChar))
            { /* 根 "C:\" 自带分隔符，避免重复 */ }
            else if (sb.Length > 0 && sb[^1] != Path.DirectorySeparatorChar)
                sb.Append(Path.DirectorySeparatorChar);
            sb.Append(part);
        }
        return sb.ToString();
    }
}
