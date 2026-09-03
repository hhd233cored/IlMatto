using System.Diagnostics;
using System.ComponentModel;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using IlMatto.Desktop.Infrastructure;
using IlMatto.Desktop.Models;
using WpfComboBox = System.Windows.Controls.ComboBox;
using WpfMessageBox = System.Windows.MessageBox;

namespace IlMatto.Desktop;

public partial class ManagerSettingsWindow : Window
{
    private readonly ManagerViewModel _viewModel;
    private readonly Dictionary<string, List<string>> _codexModelEfforts = new(StringComparer.OrdinalIgnoreCase);

    public ManagerSettingsWindow(ManagerViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;

        SelectTaggedItem(MainProviderBox, viewModel.DefaultMainAgentProvider);
        SelectTaggedItem(CodingProviderBox, viewModel.DefaultCodingAgentProvider);
        AgyPathBox.Text = viewModel.AntigravityCliPath;
        AgyModelBox.Text = viewModel.AntigravityModel;
        SelectTextItem(AgyEffortBox, viewModel.AntigravityEffort);
        AgyTimeoutBox.Text = "0";
        MainApiBaseUrlBox.Text = viewModel.MainApiBaseUrl;
        MainApiModelIdBox.Text = viewModel.MainApiModelId;
        MainApiKeyBox.Password = viewModel.MainApiKey;
        MainApiTimeoutBox.Text = viewModel.MainApiTimeoutSeconds.ToString();
        PiBaseUrlBox.Text = viewModel.BaseUrl;
        PiModelIdBox.Text = viewModel.ModelId;
        PiApiKeyBox.Password = viewModel.PiApiKey;
        CodexPathBox.Text = viewModel.CodexCliPath;
        CodexModelBox.Text = viewModel.CodexModel;
        SelectTextItem(CodexEffortBox, viewModel.CodexEffort);
        CompanionCharacterPromptBox.Text = viewModel.CompanionCharacterPrompt;
        CompanionUserProfileBox.Text = viewModel.CompanionUserProfile;
        CompanionRelationshipSummaryBox.Text = viewModel.CompanionRelationshipSummary;
        WorkspaceBox.Text = viewModel.WorkspacePath;
        SafeCommandsBox.IsChecked = viewModel.AutoApproveSafeCommands;
        GitOperationsBox.IsChecked = viewModel.AutoApproveGitOperations;
        SelectTaggedItem(AgyExecutionPolicyBox, viewModel.AntigravityExecutionPolicy);
        AgyStatusText.Text = "新会话使用 Antigravity CLI，模型回合不设应用层超时；点击“刷新 CLI 模型”检查可用模型。";
        CodexStatusText.Text = "点击“检查 Codex 状态”验证 CLI 和当前账号。";
        _viewModel.PropertyChanged += ViewModelOnPropertyChanged;
        Closed += (_, _) => _viewModel.PropertyChanged -= ViewModelOnPropertyChanged;
    }

    private async void RefreshModelsButton_OnClick(object sender, RoutedEventArgs e)
    {
        AgyStatusText.Text = "正在检查 Antigravity CLI…";
        var result = await RunCliAsync(string.IsNullOrWhiteSpace(AgyPathBox.Text) ? "agy" : AgyPathBox.Text, ["models"], 20_000);
        AgyStatusText.Text = result.Message;
        if (!result.Success) return;

        var current = NormalizeAgyModelId(AgyModelBox.Text);
        AgyModelBox.Items.Clear();
        foreach (var modelId in result.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                     .Select(ParseAgyModelId).Where(model => model.Length > 0 && model.Length < 160)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
            AgyModelBox.Items.Add(modelId);
        AgyModelBox.Text = current;
    }

    private void AgyLoginButton_OnClick(object sender, RoutedEventArgs e)
    {
        _viewModel.AntigravityCliPath = AgyPathBox.Text.Trim();
        _viewModel.OpenAntigravityLoginTerminal();
    }

    private async void RefreshCodexButton_OnClick(object sender, RoutedEventArgs e)
    {
        CodexStatusText.Text = "正在检查 Codex CLI 与登录状态…";
        try
        {
            var executable = string.IsNullOrWhiteSpace(CodexPathBox.Text) ? "codex" : CodexPathBox.Text;
            var status = await _viewModel.ProbeCodexAsync(executable);
            CodexStatusText.Text = status.Authenticated == true
                ? $"{status.Version ?? "Codex App Server"} · 已登录 · {status.Models?.Count ?? 0} 个模型"
                : $"{status.Version ?? "Codex App Server"} · 尚未登录";
            CodexPolicyText.Text = string.IsNullOrWhiteSpace(status.Policy) ? "configRequirements/read 未返回强制策略；将继承本机 Codex 配置。" : status.Policy;
            var current = CodexModelBox.Text;
            CodexModelBox.Items.Clear();
            _codexModelEfforts.Clear();
            foreach (var model in status.Models ?? new List<CodexModelInfo>())
            {
                CodexModelBox.Items.Add(model.Id);
                _codexModelEfforts[model.Id] = model.Efforts;
            }
            CodexModelBox.Text = current;
            UpdateCodexEfforts(current);
        }
        catch (Exception exception) { CodexStatusText.Text = exception.Message; }
    }

    private async void CodexLoginButton_OnClick(object sender, RoutedEventArgs e)
    {
        CodexStatusText.Text = "正在启动 Codex 浏览器登录…";
        try
        {
            var executable = string.IsNullOrWhiteSpace(CodexPathBox.Text) ? "codex" : CodexPathBox.Text;
            await _viewModel.StartCodexLoginAsync(executable);
            CodexStatusText.Text = "登录页面已在浏览器打开。完成授权后可再次检查状态。";
        }
        catch (Exception exception) { CodexStatusText.Text = exception.Message; }
    }

    private void CodexModelBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateCodexEfforts(CodexModelBox.SelectedItem?.ToString() ?? CodexModelBox.Text);

    private void UpdateCodexEfforts(string? model)
    {
        if (string.IsNullOrWhiteSpace(model) || !_codexModelEfforts.TryGetValue(model, out var efforts) || efforts.Count == 0) return;
        var current = SelectedText(CodexEffortBox, _viewModel.CodexEffort);
        CodexEffortBox.Items.Clear();
        foreach (var effort in efforts) CodexEffortBox.Items.Add(new ComboBoxItem { Content = effort });
        SelectTextItem(CodexEffortBox, efforts.Contains(current, StringComparer.OrdinalIgnoreCase) ? current : efforts[0]);
    }

    private void BrowseButton_OnClick(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "选择新 Manager 对话使用的工作区",
            UseDescriptionForTitle = true,
        };
        if (Directory.Exists(WorkspaceBox.Text)) dialog.InitialDirectory = WorkspaceBox.Text;
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK) WorkspaceBox.Text = dialog.SelectedPath;
    }

    private void SaveButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (!Directory.Exists(WorkspaceBox.Text.Trim()))
        {
            WpfMessageBox.Show(this, "请选择存在的工作区目录。", "IlMatto", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        const int agyTimeout = 0;
        if (!int.TryParse(MainApiTimeoutBox.Text, out var apiTimeout) || apiTimeout is < 10 or > 600)
        {
            WpfMessageBox.Show(this, "API 超时必须是 10 到 600 秒之间的整数。Antigravity 当前不限制等待时间。", "IlMatto", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (CompanionCharacterPromptBox.Text.Length > 8_000 || CompanionUserProfileBox.Text.Length > 8_000 || CompanionRelationshipSummaryBox.Text.Length > 8_000)
        {
            WpfMessageBox.Show(this, "陪伴设定的每个字段不能超过 8000 个字符。", "IlMatto", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var settings = new AppSettings
        {
            DefaultMainAgentProvider = SelectedTag(MainProviderBox, "antigravity"),
            DefaultCodingAgentProvider = SelectedTag(CodingProviderBox, "antigravity"),
            BaseUrl = PiBaseUrlBox.Text.Trim(),
            ModelId = PiModelIdBox.Text.Trim(),
            PiCredentialId = _viewModel.PiCredentialId,
            MainApiBaseUrl = MainApiBaseUrlBox.Text.Trim(),
            MainApiModelId = MainApiModelIdBox.Text.Trim(),
            MainApiCredentialId = _viewModel.MainApiCredentialId,
            MainApiTimeoutSeconds = apiTimeout,
            WorkspacePath = Path.GetFullPath(WorkspaceBox.Text.Trim()),
            AutoApproveSafeCommands = SafeCommandsBox.IsChecked == true,
            AutoApproveGitOperations = GitOperationsBox.IsChecked == true,
            AntigravityCliPath = AgyPathBox.Text.Trim(),
            AntigravityModel = NormalizeAgyModelId(AgyModelBox.Text),
            AntigravityEffort = SelectedText(AgyEffortBox, "medium"),
            AntigravityTimeoutSeconds = agyTimeout,
            AntigravityExecutionPolicy = SelectedTag(AgyExecutionPolicyBox, "approval"),
            CodexCliPath = CodexPathBox.Text.Trim(),
            CodexModel = CodexModelBox.Text.Trim(),
            CodexEffort = SelectedText(CodexEffortBox, "medium"),
            CodexApprovalPolicy = _viewModel.CodexApprovalPolicy,
            CodexSandboxMode = _viewModel.CodexSandboxMode,
            DefaultCompanionProfile = new ManagerCompanionProfile
            {
                CharacterPrompt = CompanionCharacterPromptBox.Text.Trim(),
                UserProfile = CompanionUserProfileBox.Text.Trim(),
                RelationshipSummary = CompanionRelationshipSummaryBox.Text.Trim(),
            },
        };
        _viewModel.ApplySettings(settings, PiApiKeyBox.Password, MainApiKeyBox.Password);
        DialogResult = true;
        Close();
    }

    private void CancelButton_OnClick(object sender, RoutedEventArgs e) { DialogResult = false; Close(); }

    private void ViewModelOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ManagerViewModel.CodingProviderStatusDetail) && !string.IsNullOrWhiteSpace(_viewModel.CodingProviderStatusDetail))
            CodexStatusText.Text = _viewModel.CodingProviderStatusDetail;
    }

    private static void SelectTaggedItem(WpfComboBox box, string value)
    {
        box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(item => string.Equals(item.Tag?.ToString(), value, StringComparison.OrdinalIgnoreCase)) ?? box.Items[0];
    }

    private static void SelectTextItem(WpfComboBox box, string value)
    {
        box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(item => string.Equals(item.Content?.ToString(), value, StringComparison.OrdinalIgnoreCase)) ?? box.Items[0];
    }

    private static string SelectedTag(WpfComboBox box, string fallback) => (box.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? fallback;
    private static string SelectedText(WpfComboBox box, string fallback) => (box.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? fallback;

    private static string ParseAgyModelId(string line)
    {
        // `agy models` prints a slug followed by a display name separated by
        // tabs or variable whitespace. Never persist the display name (or
        // the separator) as --model; AGY expects the slug only.
        var value = line.Trim().Replace("\\t", " ", StringComparison.Ordinal);
        if (value.Length == 0) return "";
        var match = Regex.Match(value, @"^(?:[*•]\s*)?([^\s]+)");
        if (!match.Success) return "";
        var id = match.Groups[1].Value.Trim();
        return id is "Available" or "Models" or "Model" or "ERROR:" ? "" : id;
    }

    private static string NormalizeAgyModelId(string? value)
    {
        var parsed = ParseAgyModelId(value ?? "");
        return parsed.Length > 0 ? parsed : (value ?? "").Trim();
    }

    private static async Task<CliResult> RunCliAsync(string executable, IReadOnlyList<string> arguments, int timeoutMs)
    {
        try
        {
            using var process = new Process { StartInfo = new ProcessStartInfo(executable.Trim()) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true } };
            foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
            process.Start();
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var cancellation = new CancellationTokenSource(timeoutMs);
            await process.WaitForExitAsync(cancellation.Token);
            var output = (await stdout).Trim();
            var error = (await stderr).Trim();
            return process.ExitCode == 0
                ? new CliResult(true, output, string.IsNullOrWhiteSpace(output) ? "检查成功。" : output)
                : new CliResult(false, output, string.IsNullOrWhiteSpace(error) ? $"CLI 退出码：{process.ExitCode}" : error);
        }
        catch (OperationCanceledException) { return new CliResult(false, "", "CLI 检查超时。"); }
        catch (Exception exception) { return new CliResult(false, "", $"无法启动 CLI：{exception.Message}"); }
    }

    private sealed record CliResult(bool Success, string Output, string Message);
}
