import assert from "node:assert/strict";
import { test } from "node:test";
import { spawn } from "node:child_process";
import net from "node:net";
import os from "node:os";
import path from "node:path";
import { mkdtemp, readFile, rm } from "node:fs/promises";
test("ManagerHost exposes a Codex draft for input-box prefill without starting Codex", async () => {
    const root = await mkdtemp(path.join(os.tmpdir(), "ilmatto-manager-codex-draft-"));
    const pipeName = `IlMatto-manager-codex-draft-${process.pid}-${Date.now()}`;
    const child = spawn(process.execPath, [path.join(path.dirname(process.argv[1] ?? ""), "index.js"), "--pipe", pipeName], {
        stdio: ["ignore", "ignore", "pipe"],
        env: { ...process.env, ILMATTO_MANAGER_RUNTIME: path.join(root, "runtime"), ILMATTO_MANAGER_LOG_DIR: path.join(root, "logs"), ILMATTO_CODEX_OBSERVATION_DIR: path.join(root, "codex-observation"), ILMATTO_MANAGER_AGENT_DIR: path.join(root, "global-agents"), ILMATTO_MANAGER_MCP_CONFIG_PATH: path.join(root, "global-mcp_config.json") },
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
            let newline = buffer.indexOf("\n");
            while (newline >= 0) {
                const line = buffer.slice(0, newline).trim();
                buffer = buffer.slice(newline + 1);
                newline = buffer.indexOf("\n");
                if (line)
                    events.push(JSON.parse(line));
            }
        });
        await waitFor(() => events.find((event) => event.type === "manager_host_ready"), () => stderr);
        socket.write(`${JSON.stringify({ type: "start_manager_session", sessionId: "session-1", workspacePath: root, antigravity: { executable: path.join(root, "missing-agy.exe") } })}\n`);
        await waitFor(() => events.find((event) => event.type === "manager_session_ready"), () => `${stderr} ${JSON.stringify(events)}`);
        const mcpConfig = JSON.parse(await readFile(path.join(root, "global-mcp_config.json"), "utf8"));
        const ilmattoMcp = mcpConfig.mcpServers?.["ilmatto-codex-observation"];
        assert.ok(ilmattoMcp);
        assert.deepEqual(ilmattoMcp.args.slice(-4), ["--pipe", `\\\\.\\pipe\\${pipeName}`, "--session-id", "session-1"]);
        socket.write(`${JSON.stringify({ type: "codex_observation_request", sessionId: "session-1", requestId: "draft-request", operation: "draft_codex_task", workspacePath: root, prompt: "请修复测试" })}\n`);
        const response = await waitFor(() => events.find((event) => event.type === "codex_observation_response" && event.requestId === "draft-request"), () => `${stderr} ${JSON.stringify(events)}`);
        assert.equal(response.ok, true);
        assert.equal(response.data.state, "awaiting_user_confirmation");
        const draft = await waitFor(() => events.find((event) => event.type === "codex_prompt_draft" && event.draftId === response.data.draftId), () => `${stderr} ${JSON.stringify(events)}`);
        assert.equal(draft.workspacePath, root);
        assert.equal(draft.text, "请修复测试");
        assert.equal(events.some((event) => event.type === "coding_interaction_request" && event.requestId === response.data.draftId), false);
        assert.equal(events.some((event) => event.type === "delegation_started"), false);
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
//# sourceMappingURL=codex-observation-host.test.js.map