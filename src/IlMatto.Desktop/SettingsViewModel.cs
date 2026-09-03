using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace IlMatto.Desktop;

public partial class SettingsViewModel : ObservableObject
{
    private readonly MainViewModel _source;

    public SettingsViewModel(MainViewModel source)
    {
        _source = source;
        BaseUrl = source.BaseUrl;
        ModelId = source.ModelId;
        ApiKey = source.ApiKey;
        WorkspacePath = source.WorkspacePath;
        AutoApproveSafeCommands = source.AutoApproveSafeCommands;
        AutoApproveGitOperations = source.AutoApproveGitOperations;
    }

    [ObservableProperty] private string baseUrl;
    [ObservableProperty] private string modelId;
    [ObservableProperty] private string apiKey;
    [ObservableProperty] private string workspacePath;
    [ObservableProperty] private bool autoApproveSafeCommands;
    [ObservableProperty] private bool autoApproveGitOperations;

    public event Action<bool>? CloseRequested;

    [RelayCommand]
    private void Save()
    {
        _source.ApplySettings(BaseUrl, ModelId, ApiKey, WorkspacePath, AutoApproveSafeCommands, AutoApproveGitOperations);
        CloseRequested?.Invoke(true);
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(false);

    [RelayCommand]
    private void BrowseWorkspace()
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "选择 Agent 工作区",
            SelectedPath = WorkspacePath,
            UseDescriptionForTitle = true
        };
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK) WorkspacePath = dialog.SelectedPath;
    }
}
