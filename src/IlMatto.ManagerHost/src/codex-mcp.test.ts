import assert from "node:assert/strict";
import { test } from "node:test";
import { spawn } from "node:child_process";
import net from "node:net";

test("IlMatto Agent Tools MCP exposes Codex, image lookup, and companion memory tools", async () => {
  const pipeName = `IlMatto-codex-mcp-test-${process.pid}-${Date.now()}`;
  const pipePath = `\\\\.\\pipe\\${pipeName}`;
  const server = net.createServer((socket) => { socket.setEncoding("utf8"); });
  await new Promise<void>((resolve) => server.listen(pipePath, resolve));
  const child = spawn(process.execPath, [process.argv[1]!.replace(/codex-mcp\.test\.js$/, "codex-mcp.js"), "--pipe", pipeName, "--session-id", "session-1"], { stdio: ["pipe", "pipe", "pipe"] });
  let output = "";
  child.stdout.setEncoding("utf8");
  child.stdout.on("data", (chunk) => { output += chunk; });
  try {
    child.stdin.write(`${JSON.stringify({ jsonrpc: "2.0", id: 1, method: "initialize", params: { protocolVersion: "2024-11-05" } })}\n`);
    child.stdin.write(`${JSON.stringify({ jsonrpc: "2.0", method: "notifications/initialized" })}\n`);
    child.stdin.write(`${JSON.stringify({ jsonrpc: "2.0", id: 2, method: "tools/list", params: {} })}\n`);
    const tools = await waitFor(() => {
      const lines = output.split(/\r?\n/).filter(Boolean);
      const line = lines.find((item) => { try { return JSON.parse(item).id === 2; } catch { return false; } });
      return line ? JSON.parse(line).result.tools : undefined;
    });
    const names = tools.map((tool: any) => tool.name);
    assert.deepEqual(names, ["draft_codex_task", "get_codex_status", "get_latest_codex_report", "get_codex_report", "get_codex_diff", "identify_image", "session_search", "session_open", "session_update", "profile_update"]);
    const identifyImage = tools.find((tool: any) => tool.name === "identify_image");
    assert.deepEqual(identifyImage.inputSchema.required, ["attachment_id"]);
    assert.equal(names.some((name: string) => ["submit_codex_task", "confirm_codex_task", "continue_codex_task", "steer_codex_task", "interrupt_codex_task"].includes(name)), false);
    const sessionSearch = tools.find((tool: any) => tool.name === "session_search");
    assert.deepEqual(sessionSearch.inputSchema.required, ["query"]);
    const sessionOpen = tools.find((tool: any) => tool.name === "session_open");
    assert.deepEqual(sessionOpen.inputSchema.required, ["session_id", "query"]);
    const sessionUpdate = tools.find((tool: any) => tool.name === "session_update");
    assert.deepEqual(sessionUpdate.inputSchema.required, ["patch"]);
    const profileUpdate = tools.find((tool: any) => tool.name === "profile_update");
    assert.deepEqual(profileUpdate.inputSchema.required, ["patch"]);
  } finally {
    child.stdin.end();
    if (child.exitCode === null) await new Promise<void>((resolve) => { const timer = setTimeout(() => { try { child.kill(); } catch { } resolve(); }, 2_000); child.once("exit", () => { clearTimeout(timer); resolve(); }); });
    await new Promise<void>((resolve) => server.close(() => resolve()));
  }
});

async function waitFor<T>(predicate: () => T | undefined): Promise<T> {
  const deadline = Date.now() + 5_000;
  while (Date.now() < deadline) { const value = predicate(); if (value !== undefined) return value; await new Promise((resolve) => setTimeout(resolve, 20)); }
  throw new Error("Timed out waiting for MCP response");
}
