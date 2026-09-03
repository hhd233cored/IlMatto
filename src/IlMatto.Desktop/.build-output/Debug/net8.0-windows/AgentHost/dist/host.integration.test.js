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
        const events = [];
        socket.setEncoding("utf8");
        let buffer = "";
        socket.on("data", (chunk) => {
            buffer += chunk;
            let index = buffer.indexOf("\n");
            while (index >= 0) {
                const line = buffer.slice(0, index).trim();
                buffer = buffer.slice(index + 1);
                index = buffer.indexOf("\n");
                if (line)
                    events.push(JSON.parse(line));
            }
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
        assert.ok(commands.commands.some((command) => command.name === "compact"));
        socket.write(`${JSON.stringify({ type: "send_message", sessionId: "test-session", text: "/help" })}\n`);
        const commandResult = await waitFor(() => events.find((event) => event.type === "command_result" && event.sessionId === "test-session" && event.command === "help"), () => stderr);
        assert.match(commandResult.message, /compact/);
        socket.write(`${JSON.stringify({ type: "shutdown" })}\n`);
        socket.end();
    }
    finally {
        if (child.exitCode === null) {
            try {
                child.kill();
            }
            catch { }
            await new Promise((resolve) => {
                const timeout = setTimeout(resolve, 2_000);
                child.once("exit", () => { clearTimeout(timeout); resolve(); });
            });
        }
        await removeWorkspace(workspace);
    }
});
async function connectWithRetry(pipePath) {
    const deadline = Date.now() + 8_000;
    while (Date.now() < deadline) {
        try {
            return await new Promise((resolve, reject) => { const socket = net.connect(pipePath); socket.once("connect", () => resolve(socket)); socket.once("error", reject); });
        }
        catch {
            await new Promise((resolve) => setTimeout(resolve, 100));
        }
    }
    throw new Error("Timed out connecting to agent host pipe");
}
async function waitFor(predicate, diagnostics) {
    const deadline = Date.now() + 8_000;
    while (Date.now() < deadline) {
        const value = predicate();
        if (value)
            return value;
        await new Promise((resolve) => setTimeout(resolve, 20));
    }
    throw new Error(`Timed out waiting for host event${diagnostics ? `: ${diagnostics()}` : ""}`);
}
async function removeWorkspace(workspace) {
    let lastError;
    for (let attempt = 0; attempt < 8; attempt++) {
        try {
            await fs.rm(workspace, { recursive: true, force: true });
            return;
        }
        catch (error) {
            lastError = error;
            if (error?.code !== "EBUSY" && error?.code !== "EPERM")
                throw error;
            await new Promise((resolve) => setTimeout(resolve, 100));
        }
    }
    throw lastError;
}
//# sourceMappingURL=host.integration.test.js.map