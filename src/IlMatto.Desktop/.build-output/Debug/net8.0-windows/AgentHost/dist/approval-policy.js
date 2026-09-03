export function isSafeCommand(command) {
    const normalized = command.trim().toLowerCase();
    if (!normalized || /(^|[;|&])\s*(remove-item|del|erase|rd|rmdir|set-content|add-content|copy-item|move-item|rename-item|format|shutdown|stop-process|taskkill)\b/.test(normalized))
        return false;
    if (/\b(invoke-webrequest|invoke-restmethod|curl|wget|start-process|set-executionpolicy|reg\s|git\s+(reset|checkout|clean|restore|commit|push|pull|merge|rebase)|npm\s+(install|uninstall|publish)|dotnet\s+(publish|pack))\b/.test(normalized))
        return false;
    return /^(get-childitem|get-content|select-string|where-object|format-table|measure-object|test-path|resolve-path|write-output|echo|pwd|dir|ls|rg\b|git\s+(status|diff|log|show|branch\b)|dotnet\s+(build|test|run\b)|npm(?:\.cmd)?\s+(test|run\s+(build|test))|node\s+--test|where\s)/.test(normalized);
}
export function isGitWriteTool(tool) {
    return ["git_stage", "git_unstage", "git_create_branch", "git_switch_branch", "git_commit"].includes(tool);
}
//# sourceMappingURL=approval-policy.js.map