export function isClientMessage(value) {
    if (!value || typeof value !== "object")
        return false;
    const type = value.type;
    return typeof type === "string" && ["start_session", "send_message", "approve_tool_call", "cancel", "new_session", "get_commands", "get_git_overview", "get_git_diff", "shutdown"].includes(type);
}
//# sourceMappingURL=protocol.js.map