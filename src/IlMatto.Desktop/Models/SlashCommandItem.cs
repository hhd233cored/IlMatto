namespace IlMatto.Desktop.Models;

public sealed record SlashCommandItem(string Name, string Description, string Source)
{
    public string DisplayName => $"/{Name}";
}
