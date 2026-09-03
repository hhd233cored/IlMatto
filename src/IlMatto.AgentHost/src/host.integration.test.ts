import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import fs from "node:fs/promises";
import net from "node:net";
import os from "node:os";
import path from "node:path";
import { test } from "node:test";

test("agent host accepts a Named Pipe connection and starts a session", async () => {
  const pipeName = `IlMatto-test-${process.pid}-${Date.now()}`;
  const pipePath = `\\\\.\\pipe\\${pipeName}`;
  const workspace = await fs.mkdtemp(path.join(os.tmpdir(), "ilmatto-host-"));
  const child = spawn(process.execPath, [path.join(path.dirname(process.argv[1] ?? ""), "index.js"), "--pipe", pipeName], { stdio: ["ignore", "ignore", "pipe"] });
  let stderr = "";
  child.stderr?.on("data", (chunk) => { stderr += chunk.toString(); });
  try {
    const socket = await connectWithRetry(pipePath);
    const events: any[] = [];
    socket.setEncoding("utf8");
    let buffer = "";
    socket.on("data", (chunk) => {
      buffer += chunk;
      let index = buffer.indexOf("\n");
      while (index >= 0) { const line = buffer.slice(0, index).trim(); buffer = buffer.slice(index + 1); index = buffer.indexOf("\n"); if (line) events.push(JSON.parse(line)); }
    });
    const first = await waitFor(() => events.find((event) => event.type === "host_ready"));
    assert.equal(first.type, "host_ready");
    socket.write(`${JSON.stringify({ type: "start_session", sessionId: "test-session", workspacePath: workspace, baseUrl: "http://127.0.0.1:1/v1", modelId: "test-model" })}\n`);
    const idle = await waitFor(() => events.find((event) => event.type === "session_state" && event.sessionId === "test-session" && event.state === "idle"), () => stderr);
    assert.equal(idle.state, "idle");
    const gitInit = await waitFor(() => events.find((event) => event.type === "tool_completed" && event.sessionId === "test-session" && event.tool === "git_init"), () => stderr);
    assert.equal(gitInit.ok, true);
    assert.equal(gitInit.autoApproved, true);
    assert.equal((await fs.lstat(path.join(workspace, ".git"))).isDirectory(), true);
    const gitOverview = await waitFor(() => events.find((event) => event.type === "git_overview" && event.sessionId === "test-session" && event.overview?.isRepository === true), () => stderr);
    assert.equal(gitOverview.overview.isRepository, true);
    const commands = await waitFor(() => events.find((event) => event.type === "slash_commands" && event.sessionId === "test-session"), () => stderr);
    assert.ok(commands.commands.some((command: any) => command.name === "compact"));
    socket.write(`${JSON.stringify({ type: "send_message", sessionId: "test-session", text: "/help" })}\n`);
    const commandResult = await waitFor(() => events.find((event) => event.type === "command_result" && event.sessionId === "test-session" && event.command === "help"), () => stderr);
    assert.match(commandResult.message, /compact/);
    socket.write(`${JSON.stringify({ type: "start_session", sessionId: "worker-session", workspacePath: workspace, baseUrl: "http://127.0.0.1:1/v1", modelId: "test-model", mode: "coding_worker" })}\n`);
    await waitFor(() => events.find((event) => event.type === "session_state" && event.sessionId === "worker-session" && event.state === "idle"), () => stderr);
    socket.write(`${JSON.stringify({ type: "send_message", sessionId: "worker-session", text: "ordinary chat is not allowed" })}\n`);
    const workerProtocolError = await waitFor(() => events.find((event) => event.type === "error" && event.sessionId === "worker-session" && event.code === "WORKER_PROTOCOL_ERROR"), () => stderr);
    assert.match(workerProtocolError.message, /code_task/);
    socket.write(`${JSON.stringify({ type: "code_task", sessionId: "worker-session", taskId: "task-1", userRequest: "/help" })}\n`);
    const missingResult = await waitFor(() => events.find((event) => event.type === "error" && event.sessionId === "worker-session" && event.code === "WORKER_RESULT_MISSING"), () => stderr);
    assert.match(missingResult.message, /submit_code_result/);
    const failedResult = events.find((event) => event.type === "code_result" && event.taskId === "task-1");
    assert.equal(failedResult?.result?.status, "failed");
    socket.write(`${JSON.stringify({ type: "shutdown" })}\n`);
    socket.end();
  } finally {
    if (child.exitCode === null) {
      try { child.kill(); } catch { }
      await new Promise<void>((resolve) => {
        const timeout = setTimeout(resolve, 2_000);
        child.once("exit", () => { clearTimeout(timeout); resolve(); });
      });
    }
    await removeWorkspace(workspace);
  }
});

test("persistent Pi sessions restore legacy transcript and can be deleted", async () => {
  const workspace = await fs.mkdtemp(path.join(os.tmpdir(), "ilmatto-persist-"));
  const first = await spawnHostSession(workspace, { restoreTranscript: [{ role: "user", text: "旧问题" }, { role: "assistant", text: "旧回答" }] });
  assert.equal(first.ready.legacyRestored, true);
  assert.equal(await fs.access(first.ready.sessionFile).then(() => true).catch(() => false), true);
  assert.match(await fs.readFile(first.ready.sessionFile, "utf8"), /旧回答/);
  await stopHost(first);

  const second = await spawnHostSession(workspace, { sessionFile: first.ready.sessionFile });
  assert.equal(second.ready.restored, true);
  assert.equal(second.ready.legacyRestored, false);
  second.socket.write(`${JSON.stringify({ type: "delete_session", sessionId: "test-session", sessionFile: first.ready.sessionFile })}\n`);
  await waitFor(() => second.events.find((event) => event.type === "session_deleted"));
  assert.equal(await fs.access(first.ready.sessionFile).then(() => true).catch(() => false), false);
  await stopHost(second);
  await removeWorkspace(workspace);
});

test("session restore rejects files outside the per-user session directory", async () => {
  const workspace = await fs.mkdtemp(path.join(os.tmpdir(), "ilmatto-session-security-"));
  const pipeName = `IlMatto-session-security-${process.pid}-${Date.now()}`;
  const pipePath = `\\\\.\\pipe\\${pipeName}`;
  const child = spawn(process.execPath, [path.join(path.dirname(process.argv[1] ?? ""), "index.js"), "--pipe", pipeName], { stdio: ["ignore", "ignore", "pipe"] });
  const socket = await connectWithRetry(pipePath);
  const events: any[] = [];
  socket.setEncoding("utf8");
  let buffer = "";
  socket.on("data", (chunk) => { buffer += chunk; let index = buffer.indexOf("\n"); while (index >= 0) { const line = buffer.slice(0, index).trim(); buffer = buffer.slice(index + 1); index = buffer.indexOf("\n"); if (line) events.push(JSON.parse(line)); } });
  try {
    await waitFor(() => events.find((event) => event.type === "host_ready"));
    socket.write(`${JSON.stringify({ type: "start_session", sessionId: "security", workspacePath: workspace, baseUrl: "http://127.0.0.1:1/v1", modelId: "test-model", sessionFile: path.join(workspace, "outside.jsonl") })}\n`);
    const error = await waitFor(() => events.find((event) => event.type === "error" && event.sessionId === "security"));
    assert.equal(error.code, "SESSION_RESTORE_INVALID");
  } finally {
    try { socket.write(`${JSON.stringify({ type: "shutdown" })}\n`); socket.end(); } catch { }
    if (child.exitCode === null) await new Promise<void>((resolve) => { const timeout = setTimeout(resolve, 2_000); child.once("exit", () => { clearTimeout(timeout); resolve(); }); });
    if (child.exitCode === null) { try { child.kill(); } catch { } }
    await removeWorkspace(workspace);
  }
});

async function spawnHostSession(workspace: string, options: { sessionFile?: string; restoreTranscript?: Array<{ role: "user" | "assistant"; text: string }> }): Promise<{ child: ReturnType<typeof spawn>; socket: net.Socket; events: any[]; ready: any }> {
  const pipeName = `IlMatto-persist-${process.pid}-${Date.now()}-${Math.random().toString(16).slice(2)}`;
  const pipePath = `\\\\.\\pipe\\${pipeName}`;
  const child = spawn(process.execPath, [path.join(path.dirname(process.argv[1] ?? ""), "index.js"), "--pipe", pipeName], { stdio: ["ignore", "ignore", "pipe"] });
  const stderr: string[] = [];
  child.stderr?.on("data", (chunk) => stderr.push(chunk.toString()));
  const socket = await connectWithRetry(pipePath);
  const events: any[] = [];
  socket.setEncoding("utf8");
  let buffer = "";
  socket.on("data", (chunk) => {
    buffer += chunk;
    let index = buffer.indexOf("\n");
    while (index >= 0) { const line = buffer.slice(0, index).trim(); buffer = buffer.slice(index + 1); index = buffer.indexOf("\n"); if (line) events.push(JSON.parse(line)); }
  });
  await waitFor(() => events.find((event) => event.type === "host_ready"), () => stderr.join(""));
  socket.write(`${JSON.stringify({ type: "start_session", sessionId: "test-session", workspacePath: workspace, baseUrl: "http://127.0.0.1:1/v1", modelId: "test-model", ...options })}\n`);
  const ready = await waitFor(() => events.find((event) => event.type === "session_ready"), () => stderr.join(""));
  return { child, socket, events, ready };
}

async function stopHost(host: { child: ReturnType<typeof spawn>; socket: net.Socket }): Promise<void> {
  try { host.socket.write(`${JSON.stringify({ type: "shutdown" })}\n`); host.socket.end(); } catch { }
  if (host.child.exitCode === null) await new Promise<void>((resolve) => { const timeout = setTimeout(resolve, 2_000); host.child.once("exit", () => { clearTimeout(timeout); resolve(); }); });
  if (host.child.exitCode === null) { try { host.child.kill(); } catch { } }
}

async function connectWithRetry(pipePath: string): Promise<net.Socket> {
  const deadline = Date.now() + 8_000;
  while (Date.now() < deadline) {
    try { return await new Promise((resolve, reject) => { const socket = net.connect(pipePath); socket.once("connect", () => resolve(socket)); socket.once("error", reject); }); }
    catch { await new Promise((resolve) => setTimeout(resolve, 100)); }
  }
  throw new Error("Timed out connecting to agent host pipe");
}

async function waitFor<T>(predicate: () => T | undefined, diagnostics?: () => string): Promise<T> {
  const deadline = Date.now() + 8_000;
  while (Date.now() < deadline) { const value = predicate(); if (value) return value; await new Promise((resolve) => setTimeout(resolve, 20)); }
  throw new Error(`Timed out waiting for host event${diagnostics ? `: ${diagnostics()}` : ""}`);
}

async function removeWorkspace(workspace: string): Promise<void> {
  let lastError: unknown;
  for (let attempt = 0; attempt < 8; attempt++) {
    try { await fs.rm(workspace, { recursive: true, force: true }); return; }
    catch (error: any) {
      lastError = error;
      if (error?.code !== "EBUSY" && error?.code !== "EPERM") throw error;
      await new Promise((resolve) => setTimeout(resolve, 100));
    }
  }
  throw lastError;
}
