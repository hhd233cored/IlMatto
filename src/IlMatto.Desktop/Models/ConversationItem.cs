using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace IlMatto.Desktop.Models;

public partial class ConversationItem : ObservableObject
{
    public ConversationItem(string sessionId, string title = "新对话")
    {
        SessionId = sessionId;
        Title = title;
        UpdatedAt = DateTime.Now;
    }

    public string SessionId { get; }

    [ObservableProperty]
    private string workspacePath = "";

    [ObservableProperty]
    private string? piSessionFile;

    [ObservableProperty]
    private string title;

    [ObservableProperty]
    private DateTime updatedAt;

    public ObservableCollection<ChatEntry> Messages { get; } = new();
    public ObservableCollection<ActivityItem> Activities { get; } = new();
    public ObservableCollection<ProcessItem> Processes { get; } = new();

    public string UpdatedLabel => UpdatedAt.Date == DateTime.Today
        ? UpdatedAt.ToString("HH:mm")
        : UpdatedAt.ToString("MM-dd");

    partial void OnUpdatedAtChanged(DateTime value) => OnPropertyChanged(nameof(UpdatedLabel));
}
