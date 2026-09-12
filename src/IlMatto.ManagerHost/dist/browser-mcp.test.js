import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { once } from "node:events";
import net from "node:net";
import path from "node:path";
import { test } from "node:test";
test("Browser MCP exposes the configured browser tool surface", async () => {
    const pipeName = `IlMatto-browser-mcp-test-${process.pid}-${Date.now()}`;
    const pipePath = `\\\\.\\pipe\\${pipeName}`;
    const managerPipe = net.createServer((socket) => socket.resume());
    await new Promise((resolve, reject) => { managerPipe.once("error", reject); managerPipe.listen(pipePath, resolve); });
    const child = spawn(process.execPath, [path.resolve(process.cwd(), "dist", "browser-mcp.js"), "--pipe", pipeName, "--session-id", "session-test"], { stdio: ["pipe", "pipe", "pipe"] });
    let output = "";
    child.stdout.setEncoding("utf8");
    child.stdout.on("data", (chunk) => { output += chunk; });
    try {
        child.stdin.write(`${JSON.stringify({ jsonrpc: "2.0", id: 1, method: "initialize", params: { protocolVersion: "2024-11-05" } })}\n`);
        child.stdin.write(`${JSON.stringify({ jsonrpc: "2.0", id: 2, method: "tools/list" })}\n`);
        await waitFor(() => output.includes('"id":2'));
        const response = output.split("\n").map((line) => line.trim()).filter(Boolean)
            .map((line) => JSON.parse(line)).find((message) => message.id === 2);
        const names = response.result.tools.map((tool) => tool.name).sort();
        assert.deepEqual(names, ["browser_click", "browser_download", "browser_evaluate", "browser_fill", "browser_keyboard", "browser_mouse", "browser_navigate", "browser_press", "browser_screenshot", "browser_scroll", "browser_snapshot", "browser_tabs", "browser_upload"]);
    }
    finally {
        child.kill();
        await once(child, "exit").catch(() => undefined);
        await new Promise((resolve) => managerPipe.close(() => resolve()));
    }
});
async function waitFor(predicate, timeoutMs = 3_000) {
    const started = Date.now();
    while (!predicate()) {
        if (Date.now() - started >= timeoutMs)
            throw new Error("Timed out waiting for Browser MCP response.");
        await new Promise((resolve) => setTimeout(resolve, 10));
    }
}
//# sourceMappingURL=browser-mcp.test.js.map