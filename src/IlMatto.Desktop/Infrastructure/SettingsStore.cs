using System.Text.Json;
using System.IO;
using IlMatto.Desktop.Models;

namespace IlMatto.Desktop.Infrastructure;

public sealed class BrowserPermissionSettings
{
    public bool Navigate { get; set; } = true;
    public bool Click { get; set; } = true;
    public bool Fill { get; set; } = true;
    public bool Press { get; set; } = true;
    public bool Scroll { get; set; } = true;
    public bool Screenshot { get; set; } = true;
    public bool Upload { get; set; }
    public bool Download { get; set; }
    public bool Evaluate { get; set; }
    public bool Coordinate { get; set; }
}

public sealed class AppSettings
{
    public string DefaultMainAgentProvider { get; set; } = "antigravity";
    public string DefaultCodingAgentProvider { get; set; } = "antigravity";
    public string BaseUrl { get; set; } = "https://api.openai.com/v1";
    public string ModelId { get; set; } = "gpt-4o-mini";
    public string PiCredentialId { get; set; } = "IlMatto/OpenAICompatible/default";
    public string MainApiBaseUrl { get; set; } = "https://api.openai.com/v1";
    public string MainApiModelId { get; set; } = "gpt-4o-mini";
    public string MainApiCredentialId { get; set; } = "IlMatto/MainAgent/OpenAICompatible/default";
    public int MainApiTimeoutSeconds { get; set; } = 120;
    public string WorkspacePath { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    public bool AutoApproveSafeCommands { get; set; }
    public bool AutoApproveGitOperations { get; set; }
    public string AntigravityCliPath { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "agy", "bin", "agy.exe");
    public string AntigravityModel { get; set; } = "";
    public string AntigravityEffort { get; set; } = "medium";
    /// <summary>Antigravity's tool permission preset.</summary>
    public string AntigravityToolPermission { get; set; } = "always-proceed";
    /// <summary>Whether Antigravity terminal commands run in the OS sandbox.</summary>
    public bool AntigravityTerminalSandbox { get; set; }
    /// <summary>Allow-list for the optional Browser MCP operations.</summary>
    public BrowserPermissionSettings BrowserPermissions { get; set; } = new();
    // Retained for settings compatibility; Antigravity turns are unbounded.
    public int AntigravityTimeoutSeconds { get; set; }
    /// <summary>approval, safe_tests, or autonomous for the isolated coding executor.</summary>
    public string AntigravityExecutionPolicy { get; set; } = "approval";
    public int TaskTraceRetentionDays { get; set; } = 30;
    public string UserId { get; set; } = "用户";
    public string UserAvatarPath { get; set; } = "";
    public string AgentAvatarPath { get; set; } = "";
    /// <summary>Use the legacy fully rendered chat list for diagnostics.</summary>
    public bool UseFullChatRendering { get; set; }
    /// <summary>The Manager conversation that was selected when the app was last closed.</summary>
    public string LastManagerSessionId { get; set; } = "";
    public ManagerCompanionProfile DefaultCompanionProfile { get; set; } = new();

}

public static class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "IlMatto",
        "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return new AppSettings();
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), JsonOptions) ?? new AppSettings();
            return settings;
        }
        catch
        {
            return new AppSettings();
        }
    }

    public static void Save(AppSettings settings)
    {
        var directory = Path.GetDirectoryName(SettingsPath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = SettingsPath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(temporaryPath, SettingsPath, overwrite: true);
    }
}
