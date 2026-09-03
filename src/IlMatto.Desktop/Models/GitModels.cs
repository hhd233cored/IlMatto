namespace IlMatto.Desktop.Models;

public sealed class GitFileChange
{
    public string Path { get; init; } = "";
    public string Status { get; init; } = "";
    public string Kind { get; init; } = "";
    public string DisplayStatus => Status switch
    {
        "M" => "M",
        "A" => "A",
        "D" => "D",
        "R" => "R",
        "??" => "?",
        _ => Status
    };
}

public sealed class GitBranch
{
    public string Name { get; init; } = "";
    public bool IsCurrent { get; init; }
    public string? Upstream { get; init; }
    public string DisplayName => IsCurrent ? $"* {Name}" : Name;
}

public sealed class GitCommit
{
    public string Id { get; init; } = "";
    public string ShortId { get; init; } = "";
    public string Subject { get; init; } = "";
    public string Author { get; init; } = "";
    public string Date { get; init; } = "";
    public string Display => string.IsNullOrWhiteSpace(ShortId) ? Subject : $"{ShortId}  {Subject}";
}

public sealed class GitRemote
{
    public string Name { get; init; } = "";
    public string? FetchUrl { get; init; }
    public string? PushUrl { get; init; }
    public string Display => string.IsNullOrWhiteSpace(FetchUrl) ? Name : $"{Name}  {FetchUrl}";
}

public sealed class GitOverview
{
    public bool IsRepository { get; init; }
    public string? Message { get; init; }
    public string? Root { get; init; }
    public string? Branch { get; init; }
    public string? Upstream { get; init; }
    public int Ahead { get; init; }
    public int Behind { get; init; }
    public List<GitFileChange>? Staged { get; init; }
    public List<GitFileChange>? Unstaged { get; init; }
    public List<GitFileChange>? Untracked { get; init; }
    public List<GitBranch>? Branches { get; init; }
    public List<GitCommit>? Commits { get; init; }
    public List<GitRemote>? Remotes { get; init; }
}
