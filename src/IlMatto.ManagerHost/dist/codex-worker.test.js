import assert from "node:assert/strict";
import { test } from "node:test";
import { EventEmitter } from "node:events";
import { Writable } from "node:stream";
import { appServerSandboxMode, buildCodexTurnInput, buildSandboxPolicy, CodexAppServerBridge, describeInteraction, extractJsonObjects, isLegacyApprovalPolicyError, itemDetails, looksLikeStructuredResultPrefix, normalizeUserInputAnswers, parseCodeResult } from "./codex-worker.js";
test("detects legacy Codex approval policy enum errors", () => {
    assert.equal(isLegacyApprovalPolicyError(new Error("Invalid request: unknown variant `unlessTrusted`, expected one of `untrusted`, `on-request`, `granular`, `never")), true);
    assert.equal(isLegacyApprovalPolicyError(new Error("Invalid request: unknown variant unlessTrusted, expected one of untrusted, on-request, granular, never")), true);
    assert.equal(isLegacyApprovalPolicyError(new Error("Invalid request: unknown variant unlessTrusted")), false);
    assert.equal(isLegacyApprovalPolicyError(new Error("Invalid request: unknown variant never, expected one of untrusted")), false);
});
test("Codex approvals map to the generic IlMatto interaction queue", () => {
    assert.deepEqual(describeInteraction("item/commandExecution/requestApproval", { reason: "需要执行测试", command: "npm test", cwd: "C:\\repo" }), {
        kind: "command_approval", title: "Codex 请求执行命令", details: "需要执行测试\nnpm test\nC:\\repo",
    });
    assert.deepEqual(describeInteraction("item/fileChange/requestApproval", { reason: "写入修复", diff: "+fixed" }), {
        kind: "file_approval", title: "Codex 请求修改文件", details: "写入修复", diff: "+fixed",
    });
    assert.equal(describeInteraction("unknown/request", {}), undefined);
});
test("Codex approval descriptions retain details from nested App Server payloads", () => {
    const command = describeInteraction("item/commandExecution/requestApproval", {
        itemId: "command-1",
        command: { argv: ["powershell.exe", "-Command", "npm test"] },
        workingDirectory: "C:\\repo",
        reason: "验证修复",
    });
    assert.equal(command?.details, "验证修复\npowershell.exe -Command npm test\nC:\\repo");
    const file = describeInteraction("item/fileChange/requestApproval", {
        itemId: "file-1",
    }, {
        id: "file-1",
        type: "fileChange",
        changes: [{ path: "src/app.ts", diff: "+fixed" }],
    });
    assert.match(String(file?.details), /文件：src\/app\.ts/);
    assert.equal(file?.diff, "+fixed");
});
test("Codex process items retain command, output and file diffs", () => {
    assert.deepEqual(itemDetails({ type: "commandExecution", command: "dotnet test", status: "completed", exitCode: 0 }), {
        tool: "run_command", command: "dotnet test", summary: "命令完成，退出码 0",
    });
    assert.deepEqual(itemDetails({ type: "fileChange", status: "completed", changes: [{ path: "src/app.ts", diff: "+ok" }] }), {
        tool: "modify_files", command: "src/app.ts", summary: "1 个文件变更", diff: "+ok",
    });
});
test("Codex user questions are returned in the App Server answer shape", () => {
    assert.deepEqual(normalizeUserInputAnswers([{ id: "choice" }], { answer: "继续" }), { choice: { answers: ["继续"] } });
    assert.deepEqual(normalizeUserInputAnswers([{ id: "targets" }], { targets: ["a", "b"] }), { targets: { answers: ["a", "b"] } });
});
test("Codex native approval policies and the always extension behave correctly", async () => {
    const sent = [];
    const child = new EventEmitter();
    child.killed = false;
    child.stdin = new Writable({
        write(chunk, _encoding, callback) {
            const message = JSON.parse(chunk.toString());
            sent.push(message);
            queueMicrotask(() => {
                if (message.method === "initialize")
                    child.stdout.emit("data", `${JSON.stringify({ id: message.id, result: {} })}\n`);
                if (message.method === "account/read")
                    child.stdout.emit("data", `${JSON.stringify({ id: message.id, result: { account: { type: "chatgpt" } } })}\n`);
                if (message.method === "configRequirements/read")
                    child.stdout.emit("data", `${JSON.stringify({ id: message.id, result: {} })}\n`);
                if (message.method === "thread/start")
                    child.stdout.emit("data", `${JSON.stringify({ id: message.id, result: { thread: { id: "thread-approval" } } })}\n`);
            });
            callback();
        },
    });
    child.stdout = new EventEmitter();
    child.stdout.setEncoding = () => undefined;
    child.stderr = new EventEmitter();
    child.stderr.setEncoding = () => undefined;
    child.stdin.end = () => { queueMicrotask(() => { child.killed = true; child.emit("exit", 0, null); }); };
    child.kill = () => { queueMicrotask(() => { child.killed = true; child.emit("exit", 0, null); }); return true; };
    const events = [];
    const bridge = new CodexAppServerBridge({ sessionId: "approval", workspacePath: ".", mode: "observation", approvalPolicy: "always", sandboxMode: "read-only" }, (event) => events.push(event), (() => child));
    await bridge.start();
    assert.equal(sent.find((message) => message.method === "thread/start")?.params.approvalPolicy, "untrusted");
    assert.equal(sent.find((message) => message.method === "thread/start")?.params.sandbox, "read-only");
    bridge.turnAutoApproveRequests = true;
    bridge.onServerRequest({ id: 41, method: "item/commandExecution/requestApproval", params: { command: "npm test" } });
    assert.equal(sent.find((message) => message.id === 41)?.result?.decision, "accept");
    assert.equal(events.some((event) => event.type === "interaction_request"), false);
    // `always` does not silently answer MCP elicitation forms; those still
    // require an explicit user response because they may request arbitrary data.
    bridge.onServerRequest({ id: 411, method: "mcpServer/elicitation/request", params: { message: "填写项目名称" } });
    assert.equal(events.some((event) => event.type === "interaction_request" && event.requestId === "codex-411"), true);
    bridge.resolve("codex-411", false);
    bridge.setApprovalPolicy("on-request");
    bridge.turnAutoApproveRequests = false;
    bridge.onServerRequest({ id: 42, method: "item/new/requestApproval", params: { reason: "未知审批类型", command: "npm test" } });
    assert.equal(events.some((event) => event.type === "interaction_request" && event.requestId === "codex-42"), true);
    assert.equal(sent.some((message) => message.id === 42), false);
    bridge.resolve("codex-42", false);
    assert.equal(sent.find((message) => message.id === 42)?.result?.decision, "decline");
    await bridge.dispose();
});
test("Codex sandbox modes use the correct lifecycle and turn spellings", () => {
    const modes = ["read-only", "workspace-write", "danger-full-access"];
    assert.deepEqual(modes.map((mode) => appServerSandboxMode(mode)), [
        "read-only", "workspace-write", "danger-full-access",
    ]);
    assert.deepEqual(modes.map((mode) => buildSandboxPolicy(mode, ".")), [
        { type: "readOnly" },
        { type: "workspaceWrite", writableRoots: [process.cwd()], networkAccess: false },
        { type: "dangerFullAccess" },
    ]);
});
test("Codex structured output is parsed without leaking concatenated JSON", () => {
    const first = JSON.stringify({ status: "completed", summaryForUser: "第一次", technicalDecisions: [], filesChanged: [], validation: [], questions: [], needsUserDecision: false });
    const second = JSON.stringify({ status: "completed", summaryForUser: "最终结果", technicalDecisions: [], filesChanged: [], validation: [], questions: [], needsUserDecision: false });
    assert.equal(looksLikeStructuredResultPrefix("{"), true);
    assert.equal(looksLikeStructuredResultPrefix("```json\n"), true);
    assert.equal(looksLikeStructuredResultPrefix("```json\n{"), true);
    assert.equal(looksLikeStructuredResultPrefix("普通回复"), false);
    assert.equal(extractJsonObjects(`${first}${second}`).length, 2);
    assert.equal(parseCodeResult(`${first}${second}`)?.summaryForUser, "最终结果");
    assert.equal(parseCodeResult("普通回复"), undefined);
});
test("Codex coding turns append local image inputs after the original text request", () => {
    const input = buildCodexTurnInput("根据截图修复布局", ["C:\\tmp\\screen.png", "C:\\tmp\\after.png"]);
    assert.equal(input[0].type, "text");
    assert.match(input[0].text, /根据截图修复布局/);
    assert.deepEqual(input.slice(1), [
        { type: "localImage", path: "C:\\tmp\\screen.png" },
        { type: "localImage", path: "C:\\tmp\\after.png" },
    ]);
});
test("Codex observation turns use plain text without output schemas", async () => {
    const sent = [];
    const child = new EventEmitter();
    child.killed = false;
    child.stdin = new Writable({
        write(chunk, _encoding, callback) {
            const message = JSON.parse(chunk.toString());
            sent.push(message);
            queueMicrotask(() => {
                if (message.method === "initialize")
                    child.stdout.emit("data", `${JSON.stringify({ id: message.id, result: {} })}\n`);
                if (message.method === "account/read")
                    child.stdout.emit("data", `${JSON.stringify({ id: message.id, result: { account: { type: "chatgpt" } } })}\n`);
                if (message.method === "configRequirements/read")
                    child.stdout.emit("data", `${JSON.stringify({ id: message.id, result: {} })}\n`);
                if (message.method === "thread/start")
                    child.stdout.emit("data", `${JSON.stringify({ id: message.id, result: { thread: { id: "thread-1" } } })}\n`);
                if (message.method === "turn/start") {
                    child.stdout.emit("data", `${JSON.stringify({ id: message.id, result: { turn: { id: "turn-1" } } })}\n`);
                    child.stdout.emit("data", `${JSON.stringify({ method: "turn/started", params: { turn: { id: "turn-1" } } })}\n`);
                    child.stdout.emit("data", `${JSON.stringify({ method: "item/agentMessage/delta", params: { itemId: "answer-1", delta: "已完成" } })}\n`);
                    child.stdout.emit("data", `${JSON.stringify({ method: "item/completed", params: { item: { id: "answer-1", type: "agentMessage", text: "已完成", phase: "finalAnswer" } } })}\n`);
                    child.stdout.emit("data", `${JSON.stringify({ method: "turn/completed", params: { turn: { status: "completed" } } })}\n`);
                }
            });
            callback();
        },
    });
    child.stdout = new EventEmitter();
    child.stdout.setEncoding = () => undefined;
    child.stderr = new EventEmitter();
    child.stderr.setEncoding = () => undefined;
    child.stdin.end = () => { queueMicrotask(() => { child.killed = true; child.emit("exit", 0, null); }); };
    child.kill = () => { queueMicrotask(() => { child.killed = true; child.emit("exit", 0, null); }); return true; };
    const events = [];
    const bridge = new CodexAppServerBridge({ sessionId: "s", workspacePath: ".", mode: "observation", approvalPolicy: "never" }, (event) => events.push(event), (() => child));
    await bridge.start();
    assert.equal(sent.find((message) => message.method === "thread/start")?.params.approvalPolicy, "never");
    bridge.sendCodeTask("task-1", "请修复测试");
    await new Promise((resolve) => setTimeout(resolve, 20));
    const turn = sent.find((message) => message.method === "turn/start");
    assert.equal(turn.params.outputSchema, undefined);
    assert.equal(turn.params.input[0].text, "请修复测试");
    assert.deepEqual(turn.params.sandboxPolicy, { type: "workspaceWrite", writableRoots: [process.cwd()], networkAccess: false });
    assert.equal(events.find((event) => event.type === "observation_result")?.text, "已完成");
    // The setter is intentionally safe to call between turns; the next
    // turn/start will carry the updated native policy.
    bridge.setApprovalPolicy("on-request");
    await bridge.dispose();
});
//# sourceMappingURL=codex-worker.test.js.map