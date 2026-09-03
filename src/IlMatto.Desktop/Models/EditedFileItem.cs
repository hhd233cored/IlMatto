using CommunityToolkit.Mvvm.ComponentModel;

namespace IlMatto.Desktop.Models;

public partial class EditedFileItem : ObservableObject
{
    public EditedFileItem(string path, int added, int removed)
    {
        Path = path;
        Added = added;
        Removed = removed;
    }

    public string Path { get; }

    [ObservableProperty]
    private int added;

    [ObservableProperty]
    private int removed;

    public string ChangeSummary => $"+{Added}  -{Removed}";

    partial void OnAddedChanged(int value) => OnPropertyChanged(nameof(ChangeSummary));
    partial void OnRemovedChanged(int value) => OnPropertyChanged(nameof(ChangeSummary));
}
