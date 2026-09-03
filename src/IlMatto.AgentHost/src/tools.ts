import { execFile, spawn } from "node:child_process";
import fs from "node:fs/promises";
import path from "node:path";
import { promisify } from "node:util";
import { assertNoSymlinkEscape, assertNotGitMetadataPath } from "./security.js";

const execFileAsync = promisify(execFile);
const MAX_OUTPUT = 1024 * 1024;

export type ApprovalRequest = { tool: string; summary: string; details: string; diff?: string; command?: string };
export type ToolApproval = (request: ApprovalRequest) => Promise<boolean>;

export async function listFiles(workspacePath: string, relativePath = "."): Promise<string> {
  const directory = assertNotGitMetadataPath(workspacePath, relativePath);
  await assertNoSymlinkEscape(workspacePath, directory);
  const entries = await fs.readdir(directory, { withFileTypes: true });
  return entries.filter((entry) => !entry.name.startsWith(".git") && entry.name !== "node_modules")
    .map((entry) => `${entry.isDirectory() ? "[dir]" : "     "} ${path.relative(workspacePath, path.join(directory, entry.name))}`)
    .join("\n");
}

export async function readFile(workspacePath: string, relativePath: string): Promise<string> {
  const file = assertNotGitMetadataPath(workspacePath, relativePath);
  await assertNoSymlinkEscape(workspacePath, file);
  return fs.readFile(file, "utf8");
}

export async function searchText(workspacePath: string, query: string): Promise<string> {
  try {
    const result = await execFileAsync("rg", ["--line-number", "--hidden", "--glob", "!.git", "--glob", "!node_modules", "--", query, workspacePath], { timeout: 30_000, maxBuffer: MAX_OUTPUT });
    return result.stdout;
  } catch (error: any) {
    if (error?.code === 1) return "No matches.";
    throw error;
  }
}

export async function git(workspacePath: string, args: string[]): Promise<string> {
  const result = await execFileAsync("git", args, { cwd: workspacePath, timeout: 30_000, maxBuffer: MAX_OUTPUT });
  return result.stdout;
}

export async function applyPatch(workspacePath: string, relativePath: string, content: string, approve: ToolApproval): Promise<string> {
  const file = assertNotGitMetadataPath(workspacePath, relativePath);
  await assertNoSymlinkEscape(workspacePath, file);
  let oldContent = "";
  try { oldContent = await fs.readFile(file, "utf8"); } catch (error: any) { if (error?.code !== "ENOENT") throw error; }
  const diff = createSimpleDiff(relativePath, oldContent, content);
  const approved = await approve({ tool: "apply_patch", summary: `修改 ${relativePath}`, details: `写入 ${content.length} 个字符`, diff });
  if (!approved) return "User denied the file change.";
  await fs.mkdir(path.dirname(file), { recursive: true });
  await fs.writeFile(file, content, "utf8");
  return `Applied change to ${relativePath}.`;
}

export async function runCommand(workspacePath: string, command: string, approve: ToolApproval, onOutput?: (chunk: string) => void): Promise<string> {
  if (/\bgit(?:\.exe)?\b/i.test(command)) throw new Error("Git commands must use the dedicated Git tools");
  const approved = await approve({ tool: "run_command", summary: "执行 PowerShell 命令", details: `cwd: ${workspacePath}\n${command}`, command });
  if (!approved) return "User denied the command.";
  return runPowerShellStreaming(workspacePath, command, onOutput);
}

function runPowerShellStreaming(workspacePath: string, command: string, onOutput?: (chunk: string) => void): Promise<string> {
  return new Promise((resolve, reject) => {
    const child = spawn("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", command], { cwd: workspacePath, windowsHide: true });
    const chunks: string[] = [];
    let total = 0;
    let outputTruncated = false;
    let settled = false;
    const append = (value: unknown) => {
      const text = Buffer.isBuffer(value) ? value.toString("utf8") : String(value ?? "");
      if (!text) return;
      const remaining = Math.max(0, MAX_OUTPUT - total);
      if (remaining > 0) {
        const visible = text.slice(0, remaining);
        chunks.push(visible);
        total += visible.length;
        if (visible) onOutput?.(visible);
      }
      if (text.length > remaining && !outputTruncated) {
        outputTruncated = true;
        onOutput?.("\n[output truncated]\n");
      }
    };
    const timer = setTimeout(() => {
      if (settled) return;
      outputTruncated = true;
      try { child.kill(); } catch { }
      const error = new Error("Command timed out after 60 seconds") as Error & { output?: string };
      error.output = `${chunks.join("")}\n[command timed out after 60 seconds]`;
      settled = true;
      reject(error);
    }, 60_000);
    child.stdout?.on("data", append);
    child.stderr?.on("data", append);
    child.once("error", (error) => {
      if (settled) return;
      clearTimeout(timer);
      settled = true;
      const wrapped = error as Error & { output?: string };
      wrapped.output = chunks.join("");
      reject(wrapped);
    });
    child.once("close", (code, signal) => {
      if (settled) return;
      clearTimeout(timer);
      settled = true;
      const output = `${chunks.join("")}${outputTruncated ? "\n[output truncated]\n" : ""}`;
      if (code && code !== 0) {
        const error = new Error(`PowerShell exited with code ${code}${signal ? ` (${signal})` : ""}`) as Error & { output?: string };
        error.output = output;
        reject(error);
        return;
      }
      resolve(truncate(output));
    });
  });
}

function truncate(value: string): string { return value.length > MAX_OUTPUT ? `${value.slice(0, MAX_OUTPUT)}\n[output truncated]` : value; }

function createSimpleDiff(relativePath: string, before: string, after: string): string {
  const oldLines = before.split("\n");
  const newLines = after.split("\n");
  const lines = [`--- a/${relativePath}`, `+++ b/${relativePath}`];
  const max = Math.max(oldLines.length, newLines.length);
  for (let i = 0; i < max; i++) {
    if (oldLines[i] === newLines[i]) lines.push(` ${oldLines[i] ?? ""}`);
    else { if (oldLines[i] !== undefined) lines.push(`-${oldLines[i]}`); if (newLines[i] !== undefined) lines.push(`+${newLines[i]}`); }
  }
  return lines.join("\n");
}
