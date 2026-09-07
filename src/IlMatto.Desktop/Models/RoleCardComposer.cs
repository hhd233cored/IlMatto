namespace IlMatto.Desktop.Models;

/// <summary>
/// Keeps the manually editable role-card body separate from the generated
/// role-name line. The generated line is stored with the snapshot so the
/// active prompt is self-describing, while the settings editor can still
/// show a clean editable body when it is reopened.
/// </summary>
public static class RoleCardComposer
{
    private const string GeneratedPrefix = "【角色名称】";

    public static string Compose(string? characterName, string? characterPrompt)
    {
        var name = NormalizeName(characterName);
        var body = RemoveGeneratedPrefix(characterPrompt);
        if (string.IsNullOrWhiteSpace(name)) return body;
        return string.IsNullOrWhiteSpace(body)
            ? $"{GeneratedPrefix}：{name}"
            : $"{GeneratedPrefix}：{name}{Environment.NewLine}{Environment.NewLine}{body}";
    }

    public static string RemoveGeneratedPrefix(string? characterPrompt)
    {
        var value = characterPrompt?.Trim() ?? "";
        if (!value.StartsWith(GeneratedPrefix, StringComparison.Ordinal)) return value;
        var separator = value.IndexOf('\n');
        return separator < 0 ? "" : value[(separator + 1)..].Trim();
    }

    public static string NormalizeName(string? characterName)
    {
        var value = characterName?.Trim() ?? "";
        return value.Length <= 80 ? value : value[..80];
    }
}
