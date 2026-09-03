namespace IlMatto.Desktop.Models;

public sealed record ApprovalRequest(
    string SessionId, string CallId, string Tool, string Summary, string Details, string? Diff,
    string Provider = "pi", string Kind = "command_approval", string? FieldsJson = null, string? Url = null)
{
    public bool RequiresInput => Kind is "question" or "mcp_form";
    public bool HasFields => !string.IsNullOrWhiteSpace(FieldsJson) && !string.Equals(FieldsJson.Trim(), "null", StringComparison.OrdinalIgnoreCase);
    public string DisplayDetails => string.IsNullOrWhiteSpace(Details) ? "请求审批的具体内容未提供。" : Details;
    public string InputHint => Kind == "mcp_form" ? "请输入符合请求 Schema 的 JSON 对象" : "请输入回复；多个字段可填写 JSON 对象";
}
