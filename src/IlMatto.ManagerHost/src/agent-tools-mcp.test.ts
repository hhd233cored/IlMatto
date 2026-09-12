import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { once } from "node:events";
import net from "node:net";
import path from "node:path";
import { test } from "node:test";

test("Agent Tools MCP exposes only companion-memory tools", async () => {
  const pipeName = `IlMatto-agent-tools-test-${process.pid}-${Date.now()}`;
  const pipePath = `\\\\.\\pipe\\${pipeName}`;
  const managerPipe = net.createServer((socket) => socket.resume());
  await new Promise<void>((resolve, reject) => {
    managerPipe.once("error", reject);
    managerPipe.listen(pipePath, resolve);
  });

  const child = spawn(process.execPath, [path.resolve(process.cwd(), "dist", "agent-tools-mcp.js"), "--pipe", pipeName, "--session-id", "session-test"], {
    stdio: ["pipe", "pipe", "pipe"],
  });
  let output = "";
  child.stdout.setEncoding("utf8");
  child.stdout.on("data", (chunk: string) => { output += chunk; });

  try {
    child.stdin.write(`${JSON.stringify({ jsonrpc: "2.0", id: 1, method: "initialize", params: { protocolVersion: "2024-11-05" } })}\n`);
    child.stdin.write(`${JSON.stringify({ jsonrpc: "2.0", id: 2, method: "tools/list" })}\n`);
    await waitFor(() => output.includes('"id":2'));
    const response = output.split("\n").map((line) => line.trim()).filter(Boolean)
      .map((line) => JSON.parse(line)).find((message) => message.id === 2);
    const names = response.result.tools.map((tool: { name: string }) => tool.name).sort();
    assert.deepEqual(names, ["profile_update", "session_open", "session_read_page", "session_search", "session_update"]);
  } finally {
    child.kill();
    await once(child, "exit").catch(() => undefined);
    await new Promise<void>((resolve) => managerPipe.close(() => resolve()));
  }
});

async function waitFor(predicate: () => boolean, timeoutMs = 3_000): Promise<void> {
  const started = Date.now();
  while (!predicate()) {
    if (Date.now() - started >= timeoutMs) throw new Error("Timed out waiting for Agent Tools MCP response.");
    await new Promise((resolve) => setTimeout(resolve, 10));
  }
}
