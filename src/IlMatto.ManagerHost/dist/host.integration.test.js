import assert from "node:assert/strict";
import { test } from "node:test";
import { spawn } from "node:child_process";
import http from "node:http";
import net from "node:net";
import os from "node:os";
import path from "node:path";
import { mkdtemp, rm } from "node:fs/promises";
test("manager host reports missing Antigravity without falling back to code execution", async () => {
    const root = await mkdtemp(path.join(os.tmpdir(), "ilmatto-manager-host-"));
    const pipeName = `IlMatto-manager-test-${process.pid}-${Date.now()}`;
    const child = spawn(process.execPath, [path.join(path.dirname(process.argv[1] ?? ""), "index.js"), "--pipe", pipeName], {
        stdio: ["ignore", "ignore", "pipe"],
        env: {
            ...process.env,
            ILMATTO_MANAGER_RUNTIME: path.join(root, "runtime"),
            ILMATTO_MANAGER_LOG_DIR: path.join(root, "logs"),
            ILMATTO_MANAGER_AGENT_DIR: path.join(root, "global-agents"),
        },
    });
    let stderr = "";
    child.stderr?.on("data", (chunk) => { stderr += chunk.toString(); });
    try {
        const socket = await connectWithRetry(`\\\\.\\pipe\\${pipeName}`);
        const events = [];
        let buffer = "";
        socket.setEncoding("utf8");
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
        await waitFor(() => events.find((event) => event.type === "manager_host_ready"), () => stderr);
        socket.write(`${JSON.stringify({ type: "start_manager_session", sessionId: "manager-1", workspacePath: root, baseUrl: "http://127.0.0.1:1/v1", modelId: "test", agyPath: path.join(root, "missing-agy.exe") })}\n`);
        const ready = await waitFor(() => events.find((event) => event.type === "manager_session_ready"), () => stderr);
        assert.equal(ready.antigravityAvailable, false);
        socket.write(`${JSON.stringify({ type: "send_manager_message", sessionId: "manager-1", text: "Please edit the project" })}\n`);
        const error = await waitFor(() => events.find((event) => event.type === "manager_error" && event.code === "AGY_NOT_FOUND"), () => stderr);
        assert.match(error.message, /missing-agy|ENOENT|not found/i);
        assert.equal(events.some((event) => event.type === "delegation_started"), false);
        socket.write(`${JSON.stringify({ type: "shutdown" })}\n`);
        socket.end();
    }
    finally {
        if (child.exitCode === null) {
            await new Promise((resolve) => { const timer = setTimeout(() => { try {
                child.kill();
            }
            catch { } resolve(); }, 2_000); child.once("exit", () => { clearTimeout(timer); resolve(); }); });
        }
        await rm(root, { recursive: true, force: true });
    }
});
test("manager host accepts the unified start shape and never creates a coding worker", async () => {
    const root = await mkdtemp(path.join(os.tmpdir(), "ilmatto-manager-host-unified-"));
    const pipeName = `IlMatto-manager-unified-${process.pid}-${Date.now()}`;
    const child = spawn(process.execPath, [path.join(path.dirname(process.argv[1] ?? ""), "index.js"), "--pipe", pipeName], {
        stdio: ["ignore", "ignore", "pipe"],
        env: { ...process.env, ILMATTO_MANAGER_RUNTIME: path.join(root, "runtime"), ILMATTO_MANAGER_LOG_DIR: path.join(root, "logs"), ILMATTO_MANAGER_AGENT_DIR: path.join(root, "global-agents") },
    });
    let stderr = "";
    child.stderr?.on("data", (chunk) => { stderr += chunk.toString(); });
    try {
        const socket = await connectWithRetry(`\\\\.\\pipe\\${pipeName}`);
        const events = [];
        let buffer = "";
        socket.setEncoding("utf8");
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
        await waitFor(() => events.find((event) => event.type === "manager_host_ready"), () => stderr);
        socket.write(`${JSON.stringify({ type: "start_manager_session", sessionId: "unified-1", workspacePath: root, antigravity: { executable: path.join(root, "missing-agy.exe"), model: "gemini-test", effort: "medium" } })}\n`);
        const ready = await waitFor(() => events.find((event) => event.type === "manager_session_ready"), () => `${stderr} events=${JSON.stringify(events)}`);
        assert.equal(ready.mainProvider, "antigravity");
        assert.equal(ready.codingProvider, "antigravity");
        socket.write(`${JSON.stringify({ type: "send_manager_message", sessionId: "unified-1", text: "请创建项目" })}\n`);
        await waitFor(() => events.find((event) => event.type === "manager_error" && event.code === "AGY_NOT_FOUND"), () => `${stderr} events=${JSON.stringify(events)}`);
        assert.equal(events.some((event) => ["delegation_started", "code_result", "verification_started"].includes(event.type)), false);
        socket.write(`${JSON.stringify({ type: "shutdown" })}\n`);
        socket.end();
    }
    finally {
        if (child.exitCode === null)
            await new Promise((resolve) => { const timer = setTimeout(() => { try {
                child.kill();
            }
            catch { } resolve(); }, 2_000); child.once("exit", () => { clearTimeout(timer); resolve(); }); });
        await rm(root, { recursive: true, force: true });
    }
});
test("manager host fails fast when Antigravity is not authenticated", async () => {
    const root = await mkdtemp(path.join(os.tmpdir(), "ilmatto-manager-host-auth-"));
    const pipeName = `IlMatto-manager-auth-test-${process.pid}-${Date.now()}`;
    const child = spawn(process.execPath, [path.join(path.dirname(process.argv[1] ?? ""), "index.js"), "--pipe", pipeName], {
        stdio: ["ignore", "ignore", "pipe"],
        env: {
            ...process.env,
            ILMATTO_MANAGER_RUNTIME: path.join(root, "runtime"),
            ILMATTO_MANAGER_LOG_DIR: path.join(root, "logs"),
            ILMATTO_MANAGER_AGENT_DIR: path.join(root, "global-agents"),
        },
    });
    let stderr = "";
    child.stderr?.on("data", (chunk) => { stderr += chunk.toString(); });
    try {
        const socket = await connectWithRetry(`\\\\.\\pipe\\${pipeName}`);
        const events = [];
        let buffer = "";
        socket.setEncoding("utf8");
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
        await waitFor(() => events.find((event) => event.type === "manager_host_ready"), () => stderr);
        // Node itself is a deterministic stand-in: --version succeeds, while the
        // `models` probe exits without an authentication result. The manager must
        // reject this state before starting a hidden long-lived coordinator.
        socket.write(`${JSON.stringify({ type: "start_manager_session", sessionId: "manager-auth-1", workspacePath: root, baseUrl: "http://127.0.0.1:1/v1", modelId: "test", agyPath: process.execPath })}\n`);
        await waitFor(() => events.find((event) => event.type === "manager_session_ready"), () => stderr);
        const startedAt = Date.now();
        socket.write(`${JSON.stringify({ type: "send_manager_message", sessionId: "manager-auth-1", text: "你好，陪我聊聊天。" })}\n`);
        const error = await waitFor(() => events.find((event) => event.type === "manager_error" && event.code === "AGY_AUTH_REQUIRED"), () => `${stderr} events=${JSON.stringify(events)}`);
        assert.match(error.message, /未认证|登录|authenticated|login/i);
        assert.ok(Date.now() - startedAt < 3_000, `authentication failure took too long: ${Date.now() - startedAt}ms`);
        assert.equal(events.some((event) => event.type === "delegation_started"), false);
        socket.write(`${JSON.stringify({ type: "shutdown" })}\n`);
        socket.end();
    }
    finally {
        if (child.exitCode === null) {
            await new Promise((resolve) => { const timer = setTimeout(() => { try {
                child.kill();
            }
            catch { } resolve(); }, 2_000); child.once("exit", () => { clearTimeout(timer); resolve(); }); });
        }
        await rm(root, { recursive: true, force: true });
    }
});
test("manager host forwards streamed companion text once without a final duplicate", async () => {
    const root = await mkdtemp(path.join(os.tmpdir(), "ilmatto-manager-host-stream-"));
    const coordinatorDirectory = path.join(root, "coordinator");
    const apiServer = http.createServer(async (_request, response) => {
        response.setHeader("content-type", "text/event-stream");
        response.write(`data: ${JSON.stringify({ choices: [{ delta: { content: '{"schemaVersion":1,"action":"respond","message":"你好' } }] })}\n\n`);
        response.write(`data: ${JSON.stringify({ choices: [{ delta: { content: '，世界！"}' } }] })}\n\n`);
        response.end("data: [DONE]\n\n");
    });
    await new Promise((resolve) => apiServer.listen(0, "127.0.0.1", resolve));
    const address = apiServer.address();
    assert.ok(address && typeof address === "object");
    const pipeName = `IlMatto-manager-stream-${process.pid}-${Date.now()}`;
    const child = spawn(process.execPath, [path.join(path.dirname(process.argv[1] ?? ""), "index.js"), "--pipe", pipeName], {
        stdio: ["ignore", "ignore", "pipe"],
        env: {
            ...process.env,
            ILMATTO_COORDINATOR_SESSION_DIR: coordinatorDirectory,
            ILMATTO_MANAGER_RUNTIME: path.join(root, "runtime"),
            ILMATTO_MANAGER_LOG_DIR: path.join(root, "logs"),
            ILMATTO_MANAGER_AGENT_DIR: path.join(root, "global-agents"),
        },
    });
    let stderr = "";
    child.stderr?.on("data", (chunk) => { stderr += chunk.toString(); });
    let socket;
    try {
        socket = await connectWithRetry(`\\\\.\\pipe\\${pipeName}`);
        const events = [];
        let buffer = "";
        socket.setEncoding("utf8");
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
        await waitFor(() => events.find((event) => event.type === "manager_host_ready"), () => stderr);
        socket.write(`${JSON.stringify({
            type: "start_manager_session", sessionId: "manager-stream-1", workspacePath: root,
            mainAgent: { provider: "openai_compatible", baseUrl: `http://127.0.0.1:${address.port}/v1`, modelId: "test-model" },
            codingAgent: { provider: "pi", baseUrl: "http://127.0.0.1:1/v1", modelId: "test-model" },
        })}\n`);
        await waitFor(() => events.find((event) => event.type === "manager_session_ready"), () => `${stderr} events=${JSON.stringify(events)}`);
        socket.write(`${JSON.stringify({ type: "send_manager_message", sessionId: "manager-stream-1", text: "普通问题" })}\n`);
        const completion = await waitFor(() => events.find((event) => event.type === "manager_completed" && event.action === "respond"), () => `${stderr} events=${JSON.stringify(events)}`);
        assert.equal(completion.final, true);
        const deltas = events.filter((event) => event.type === "manager_delta").map((event) => event.text);
        assert.deepEqual(deltas, ["你好", "，世界！"]);
        assert.equal(deltas.join(""), "你好，世界！");
    }
    finally {
        try {
            socket?.write(`${JSON.stringify({ type: "shutdown" })}\n`);
            socket?.end();
        }
        catch { }
        if (child.exitCode === null) {
            await new Promise((resolve) => { const timer = setTimeout(() => { try {
                child.kill();
            }
            catch { } resolve(); }, 2_000); child.once("exit", () => { clearTimeout(timer); resolve(); }); });
        }
        await new Promise((resolve) => apiServer.close(() => resolve()));
        await rm(root, { recursive: true, force: true });
    }
});
async function connectWithRetry(pipePath) {
    const deadline = Date.now() + 8_000;
    while (Date.now() < deadline) {
        try {
            return await new Promise((resolve, reject) => { const socket = net.createConnection(pipePath); socket.once("connect", () => resolve(socket)); socket.once("error", reject); });
        }
        catch {
            await new Promise((resolve) => setTimeout(resolve, 40));
        }
    }
    throw new Error("Timed out connecting to Manager Host");
}
async function waitFor(predicate, diagnostics) {
    const deadline = Date.now() + 10_000;
    while (Date.now() < deadline) {
        const value = predicate();
        if (value)
            return value;
        await new Promise((resolve) => setTimeout(resolve, 20));
    }
    throw new Error(`Timed out waiting for Manager Host event: ${diagnostics()}`);
}
//# sourceMappingURL=host.integration.test.js.map