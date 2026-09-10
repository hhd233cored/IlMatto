using CommunityToolkit.Mvvm.ComponentModel;
using System.Text.RegularExpressions;

namespace IlMatto.Desktop.Models;

public partial class ProcessItem : ObservableObject
{
    private const int MaxDetailsLength = 16_000;
    public ProcessItem(string kind, string title, string status, string details = "", string? callId = null, string? commandLine = null)
    {
        Kind = kind;
        Title = title;
        Status = status;
        Details = details;
        CallId = callId;
        CommandLine = commandLine ?? "";
        IsExpanded = kind == "思路";
    }

    public string Kind { get; }
    public string? CallId { get; }
    public bool IsVisibleOperation =>
        string.Equals(Kind, "思路", StringComparison.Ordinal) ||
        // ManagerWindow uses the same inline timeline as the Pi workbench.
        // Generic tool rows are intentionally visible there as well, while
        // the existing Pi process panel can continue to filter its own rows.
        string.Equals(Kind, "工具", StringComparison.Ordinal) ||
        // Inline thinking rows are created without the right-panel kind so
        // they can sit between streamed text segments. Keep them visible in
        // the transcript as well as in the process panel.
        Title.StartsWith("正在思考", StringComparison.Ordinal) ||
        string.Equals(Title, "run_command", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Title, "apply_patch", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Title, "执行 PowerShell 命令", StringComparison.OrdinalIgnoreCase) ||
        Title.StartsWith("修改 ", StringComparison.Ordinal);

    private static string FirstLine(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var firstLine = value.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n', 2)[0].Trim();
        return firstLine;
    }

    [ObservableProperty]
    private string title;

    private string commandLine = "";
    public string CommandLine
    {
        get => commandLine;
        set
        {
            var normalized = value?.Trim() ?? "";
            if (commandLine == normalized) return;
            commandLine = normalized;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CommandPreview));
        }
    }

    public string CommandPreview => FirstLine(CommandLine);

    private string status = "";
    public string Status
    {
        get => status;
        set
        {
            if (status == value) return;
            status = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DisplayTitle));
        }
    }

    private string details = "";
    public string Details
    {
        get => details;
        set
        {
            var normalized = LimitDetails(value);
            if (details == normalized) return;
            details = normalized;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DisplayTitle));
        }
    }

    [ObservableProperty]
    private bool isExpanded;

    public string ToggleGlyph => IsExpanded ? "⌄" : "›";

    public string DisplayTitle
    {
        get
        {
            if (!string.Equals(Title, "正在思考", StringComparison.Ordinal)) return NormalizePreview(Title);
            var excerpt = Excerpt(Details);
            if (string.IsNullOrWhiteSpace(excerpt)) return Status == "进行中" ? "正在思考" : "思考完成";
            return Status == "进行中" ? $"正在思考 {excerpt}" : excerpt;
        }
    }

    public bool IsThinking => Title.StartsWith("正在思考", StringComparison.Ordinal);

    private static string Excerpt(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var normalized = NormalizePreview(value);
        const int maxLength = 96;
        return normalized.Length > maxLength ? normalized[..maxLength].TrimEnd() + "…" : normalized;
    }

    /// <summary>
    /// Operation headers are metadata labels, not assistant prose. Render them
    /// as a compact plain-text preview so Markdown markers from agent thinking
    /// summaries cannot leak into the collapsed row or break truncation.
    /// </summary>
    private static string NormalizePreview(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var normalized = value.Replace("\r\n", "\n").Replace('\r', '\n');
        normalized = Regex.Replace(normalized, @"\[([^\]]+)\]\([^\)]*\)", "$1");
        normalized = Regex.Replace(normalized, @"[`*_~]", "");
        normalized = Regex.Replace(normalized, @"\s+", " ");
        return normalized.Trim();
    }

    partial void OnIsExpandedChanged(bool value) => OnPropertyChanged(nameof(ToggleGlyph));
    partial void OnTitleChanged(string value)
    {
        OnPropertyChanged(nameof(DisplayTitle));
        OnPropertyChanged(nameof(IsThinking));
        OnPropertyChanged(nameof(IsVisibleOperation));
    }
    private static string LimitDetails(string? value)
    {
        value ??= "";
        return value.Length <= MaxDetailsLength ? value : value[..MaxDetailsLength].TrimEnd() + "\n…（内容已截断）";
    }
}
