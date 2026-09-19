using System.Collections.Concurrent;
using System.IO;
using System.Threading.Channels;
using WdsShell.Core.Engine;

namespace WdsShell.Core.Cleanup;

/// <summary>
/// Consumes scan files while a scan is running. Path matching happens before
/// the queue, so the analyzer never stores the entire disk scan in memory.
/// </summary>
public sealed class CleanupAnalyzer : IDisposable
{
    private readonly IReadOnlyList<CleanupRule> _rules;
    private readonly Channel<ScanFile> _pending = Channel.CreateUnbounded<ScanFile>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly ConcurrentQueue<CleanupCandidate> _ready = new();
    private readonly ConcurrentDictionary<string, byte> _seen = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _worker;

    public CleanupAnalyzer(IReadOnlyList<CleanupRule> rules)
    {
        _rules = rules;
        _worker = Task.Run(ProcessAsync);
    }

    public void Accept(ScanFile file)
    {
        if (_stop.IsCancellationRequested ||
            file.Attributes.HasFlag(FileAttributes.Directory) ||
            file.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
            file.Attributes.HasFlag(FileAttributes.Temporary) ||
            file.Attributes.HasFlag(FileAttributes.Offline)) return;

        try
        {
            var normalized = file with { FullPath = Path.GetFullPath(file.FullPath) };
            if (_rules.Any(rule => rule.IsPathAllowed(normalized.FullPath)))
                _pending.Writer.TryWrite(normalized);
        }
        catch (ArgumentException)
        {
            // Invalid paths are not cleanup candidates.
        }
    }

    public CleanupCandidate[] Drain(int maximum = 512)
    {
        var result = new List<CleanupCandidate>(Math.Min(maximum, 128));
        while (result.Count < maximum && _ready.TryDequeue(out var candidate))
            result.Add(candidate);
        return result.ToArray();
    }

    public void Complete()
    {
        _pending.Writer.TryComplete();
    }

    private async Task ProcessAsync()
    {
        try
        {
            await foreach (var file in _pending.Reader.ReadAllAsync(_stop.Token))
            {
                foreach (var rule in _rules)
                {
                    if (!rule.IsPathAllowed(file.FullPath)) continue;
                    if (TryCreateCandidate(file, rule) is { } candidate &&
                        _seen.TryAdd(candidate.FullPath, 0))
                        _ready.Enqueue(candidate);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static CleanupCandidate? TryCreateCandidate(ScanFile file, CleanupRule rule)
    {
        if (file.Attributes.HasFlag(FileAttributes.Hidden) ||
            file.Attributes.HasFlag(FileAttributes.System) ||
            file.Attributes.HasFlag(FileAttributes.ReadOnly) ||
            file.Attributes.HasFlag(FileAttributes.Temporary) ||
            file.Attributes.HasFlag(FileAttributes.Offline)) return null;

        try
        {
            var attributes = File.GetAttributes(file.FullPath);
            if (attributes.HasFlag(FileAttributes.Directory) ||
                attributes.HasFlag(FileAttributes.ReparsePoint) ||
                attributes.HasFlag(FileAttributes.Hidden) ||
                attributes.HasFlag(FileAttributes.System) ||
                attributes.HasFlag(FileAttributes.ReadOnly) ||
                attributes.HasFlag(FileAttributes.Temporary) ||
                attributes.HasFlag(FileAttributes.Offline)) return null;

            var info = new FileInfo(file.FullPath);
            if (!info.Exists) return null;
            var lastWrite = info.LastWriteTimeUtc;
            if (DateTime.UtcNow - lastWrite < TimeSpan.FromDays(rule.MinimumAgeDays)) return null;

            return new CleanupCandidate(
                rule.Id,
                rule.Category,
                rule.Description,
                info.FullName,
                info.Length,
                lastWrite,
                file.Node,
                rule.DefaultSelected);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        _pending.Writer.TryComplete();
        _stop.Cancel();
        try { _worker.Wait(TimeSpan.FromSeconds(1)); } catch { }
        _stop.Dispose();
    }
}
