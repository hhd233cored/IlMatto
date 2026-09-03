using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;

namespace IlMatto.Desktop.Models;

public partial class ChatSegment : ObservableObject
{
    public ChatSegment(string kind, string text = "")
    {
        Kind = kind;
        Text = text;
        Operations.CollectionChanged += OnOperationsChanged;
    }

    public string Kind { get; }
    public bool IsText => Kind == "text";
    public bool IsOperations => Kind == "operations";

    [ObservableProperty]
    private string text;

    [ObservableProperty]
    private bool isExpanded;

    public ObservableCollection<ProcessItem> Operations { get; } = new();
    public ProcessItem? LatestOperation => Operations.LastOrDefault(item => item.IsVisibleOperation);
    public IEnumerable<ProcessItem> PreviousOperations
    {
        get
        {
            var visible = Operations.Where(item => item.IsVisibleOperation).ToList();
            return visible.Count > 1 ? visible.Take(visible.Count - 1) : Array.Empty<ProcessItem>();
        }
    }
    public bool HasVisibleOperations => Operations.Any(item => item.IsVisibleOperation);
    public bool HasPreviousOperations => Operations.Count(item => item.IsVisibleOperation) > 1;
    public string ToggleGlyph => IsExpanded ? "⌄" : "›";

    private void OnOperationsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(LatestOperation));
        OnPropertyChanged(nameof(PreviousOperations));
        OnPropertyChanged(nameof(HasPreviousOperations));
        OnPropertyChanged(nameof(HasVisibleOperations));
    }

    partial void OnIsExpandedChanged(bool value) => OnPropertyChanged(nameof(ToggleGlyph));
}
