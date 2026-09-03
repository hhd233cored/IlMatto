using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace IlMatto.Desktop.Models;

public partial class ChatEntry : ObservableObject
{
    public ChatEntry(string role, string text)
    {
        Role = role;
        Text = text;
        if (!string.IsNullOrEmpty(text)) Segments.Add(new ChatSegment("text", text));
    }
    public string Role { get; }
    [ObservableProperty] private string text;
    public ObservableCollection<ChatSegment> Segments { get; } = new();
    public ObservableCollection<EditedFileItem> EditedFiles { get; } = new();
    [ObservableProperty] private ApprovalRequest? pendingApproval;
    [ObservableProperty] private bool isCompleted;

    public bool HasPendingApproval => PendingApproval is not null;
    public bool HasEditedFiles => EditedFiles.Count > 0 && IsCompleted;
    public string EditedFilesSummary => $"已编辑 {EditedFiles.Count} 个文件";

    public void AppendText(string value)
    {
        if (string.IsNullOrEmpty(value)) return;
        Text += value;
        var last = Segments.LastOrDefault();
        if (last?.IsText == true)
            last.Text += value;
        else
            Segments.Add(new ChatSegment("text", value));
    }

    public ChatSegment AppendOperation(ProcessItem operation)
    {
        var group = Segments.LastOrDefault();
        if (group?.IsOperations != true)
        {
            group = new ChatSegment("operations") { IsExpanded = false };
            Segments.Add(group);
        }
        group.Operations.Add(operation);
        return group;
    }

    public void AddEditedFile(string path, int added, int removed)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        var normalized = path.Replace('\\', '/').Trim();
        var existing = EditedFiles.FirstOrDefault(item => string.Equals(item.Path, normalized, StringComparison.OrdinalIgnoreCase));
        if (existing is null) EditedFiles.Add(new EditedFileItem(normalized, added, removed));
        else { existing.Added += added; existing.Removed += removed; }
        OnPropertyChanged(nameof(HasEditedFiles));
        OnPropertyChanged(nameof(EditedFilesSummary));
    }

    partial void OnPendingApprovalChanged(ApprovalRequest? value) => OnPropertyChanged(nameof(HasPendingApproval));
    partial void OnIsCompletedChanged(bool value) => OnPropertyChanged(nameof(HasEditedFiles));
}
