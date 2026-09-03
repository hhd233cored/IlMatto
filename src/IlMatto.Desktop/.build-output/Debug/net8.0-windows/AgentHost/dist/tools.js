import { execFile } from "node:child_process";
import fs from "node:fs/promises";
import path from "node:path";
import { promisify } from "node:util";
import { assertNoSymlinkEscape, assertNotGitMetadataPath } from "./security.js";
const execFileAsync = promisify(execFile);
const MAX_OUTPUT = 1024 * 1024;
export async function listFiles(workspacePath, relativePath = ".") {
    const directory = assertNotGitMetadataPath(workspacePath, relativePath);
    await assertNoSymlinkEscape(workspacePath, directory);
    const entries = await fs.readdir(directory, { withFileTypes: true });
    return entries.filter((entry) => !entry.name.startsWith(".git") && entry.name !== "node_modules")
        .map((entry) => `${entry.isDirectory() ? "[dir]" : "     "} ${path.relative(workspacePath, path.join(directory, entry.name))}`)
        .join("\n");
}
export async function readFile(workspacePath, relativePath) {
    const file = assertNotGitMetadataPath(workspacePath, relativePath);
    await assertNoSymlinkEscape(workspacePath, file);
    return fs.readFile(file, "utf8");
}
export async function searchText(workspacePath, query) {
    try {
        const result = await execFileAsync("rg", ["--line-number", "--hidden", "--glob", "!.git", "--glob", "!node_modules", "--", query, workspacePath], { timeout: 30_000, maxBuffer: MAX_OUTPUT });
        return result.stdout;
    }
    catch (error) {
        if (error?.code === 1)
            return "No matches.";
        throw error;
    }
}
export async function git(workspacePath, args) {
    const result = await execFileAsync("git", args, { cwd: workspacePath, timeout: 30_000, maxBuffer: MAX_OUTPUT });
    return result.stdout;
}
export async function applyPatch(workspacePath, relativePath, content, approve) {
    const file = assertNotGitMetadataPath(workspacePath, relativePath);
    await assertNoSymlinkEscape(workspacePath, file);
    let oldContent = "";
    try {
        oldContent = await fs.readFile(file, "utf8");
    }
    catch (error) {
        if (error?.code !== "ENOENT")
            throw error;
    }
    const diff = createSimpleDiff(relativePath, oldContent, content);
    const approved = await approve({ tool: "apply_patch", summary: `修改 ${relativePath}`, details: `写入 ${content.length} 个字符`, diff });
    if (!approved)
        return "User denied the file change.";
    await fs.mkdir(path.dirname(file), { recursive: true });
    await fs.writeFile(file, content, "utf8");
    return `Applied change to ${relativePath}.`;
}
export async function runCommand(workspacePath, command, approve) {
    if (/\bgit(?:\.exe)?\b/i.test(command))
        throw new Error("Git commands must use the dedicated Git tools");
    const approved = await approve({ tool: "run_command", summary: "执行 PowerShell 命令", details: `cwd: ${workspacePath}\n${command}`, command });
    if (!approved)
        return "User denied the command.";
    const result = await execFileAsync("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", command], { cwd: workspacePath, timeout: 60_000, maxBuffer: MAX_OUTPUT, windowsHide: true });
    return truncate(`${result.stdout}${result.stderr ? `\n${result.stderr}` : ""}`);
}
function truncate(value) { return value.length > MAX_OUTPUT ? `${value.slice(0, MAX_OUTPUT)}\n[output truncated]` : value; }
function createSimpleDiff(relativePath, before, after) {
    const oldLines = before.split("\n");
    const newLines = after.split("\n");
    const lines = [`--- a/${relativePath}`, `+++ b/${relativePath}`];
    const max = Math.max(oldLines.length, newLines.length);
    for (let i = 0; i < max; i++) {
        if (oldLines[i] === newLines[i])
            lines.push(` ${oldLines[i] ?? ""}`);
        else {
            if (oldLines[i] !== undefined)
                lines.push(`-${oldLines[i]}`);
            if (newLines[i] !== undefined)
                lines.push(`+${newLines[i]}`);
        }
    }
    return lines.join("\n");
}
//# sourceMappingURL=tools.js.map