using System.Text.Json;
using System.IO;
using IlMatto.Desktop.Models;

namespace IlMatto.Desktop.Infrastructure;

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
    // Retained for settings compatibility; Antigravity turns are unbounded.
    public int AntigravityTimeoutSeconds { get; set; }
    /// <summary>approval, safe_tests, or autonomous for the isolated coding executor.</summary>
    public string AntigravityExecutionPolicy { get; set; } = "approval";
    public int TaskTraceRetentionDays { get; set; } = 30;
    public string CodexCliPath { get; set; } = FindCodexCli();
    public string CodexModel { get; set; } = "";
    public string CodexEffort { get; set; } = "medium";
    /// <summary>Codex native approval policy plus IlMatto's always auto-accept extension.</summary>
    public string CodexApprovalPolicy { get; set; } = "on-request";
    /// <summary>Native Codex sandbox mode: read-only, workspace-write, or danger-full-access.</summary>
    public string CodexSandboxMode { get; set; } = "workspace-write";
    public string UserId { get; set; } = "用户";
    public string UserAvatarPath { get; set; } = "";
    public string AgentAvatarPath { get; set; } = "";
    /// <summary>Use the legacy fully rendered chat list for diagnostics.</summary>
    public bool UseFullChatRendering { get; set; }
    public ManagerCompanionProfile DefaultCompanionProfile { get; set; } = new();

    internal static string FindCodexCli()
    {
        try
        {
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex", "bin");
            if (Directory.Exists(root))
            {
                var candidate = Directory.EnumerateFiles(root, "codex.exe", SearchOption.AllDirectories)
                    .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(candidate)) return candidate;
            }
        }
        catch { }
        return "codex";
    }
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
            // Codex Desktop stores versioned binaries below
            // %LOCALAPPDATA%\\OpenAI\\Codex\\bin. A client update can remove
            // the directory referenced by an older IlMatto setting, leaving a
            // stale absolute path that later fails with ENOENT. Re-discover a
            // current binary at load time while preserving user-specified PATH
            // commands and custom relative values.
            if (IsMissingAbsolutePath(settings.CodexCliPath))
            {
                var discovered = AppSettings.FindCodexCli();
                if (!string.Equals(discovered, "codex", StringComparison.OrdinalIgnoreCase))
                    settings.CodexCliPath = discovered;
            }
            return settings;
        }
        catch
        {
            return new AppSettings();
        }
    }

    private static bool IsMissingAbsolutePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        try { return Path.IsPathRooted(value) && !File.Exists(value); }
        catch { return false; }
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
