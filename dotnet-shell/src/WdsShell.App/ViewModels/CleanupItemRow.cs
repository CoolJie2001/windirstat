using CommunityToolkit.Mvvm.ComponentModel;
using WdsShell.Core.Cleanup;
using WdsShell.Core.Layout;

namespace WdsShell.App.ViewModels;

public sealed partial class CleanupItemRow : ObservableObject
{
    public CleanupItemRow(CleanupCandidate candidate)
    {
        Candidate = candidate;
        IsSelected = candidate.DefaultSelected;
    }

    public CleanupCandidate Candidate { get; }
    public string Name => Path.GetFileName(Candidate.FullPath);
    public string PathText => Candidate.FullPath;
    public string Category => Candidate.Category;
    public string Reason => Candidate.Description;
    public string SizeText => SizeFormat.Format(Candidate.Size);
    public string ModifiedText => Candidate.LastWriteTimeUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    [ObservableProperty] private bool _isSelected;
}
