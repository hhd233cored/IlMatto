import assert from "node:assert/strict";
import { test } from "node:test";
import { containsForbiddenInteractiveActivity, extractManagerMessageIncrementally, InteractiveCliGateway, parseInteractiveAction, stripTerminalControlSequences } from "./antigravity-interactive.js";
import type { ManagerHostMessage } from "./protocol.js";

test("interactive output parser removes terminal control sequences", () => {
  assert.equal(stripTerminalControlSequences("\u001b[2K\u001b[1Ghello\rworld"), "helloworld");
});

test("interactive output parser emits only the manager message", () => {
  const partial = extractManagerMessageIncrementally('{"schemaVersion":1,"action":"respond","message":"你好');
  assert.equal(partial.found, true);
  assert.equal(partial.text, "你好");
  assert.deepEqual(parseInteractiveAction('{"schemaVersion":1,"action":"respond","message":"你好"}'), {
    schemaVersion: 1, action: "respond", message: "你好",
  });
});

test("interactive output parser ignores ordinary terminal JSON", () => {
  assert.equal(parseInteractiveAction('{"status":"thinking","message":"not a manager action"}'), undefined);
});

test("interactive policy detector rejects tool and subagent events", () => {
  assert.equal(containsForbiddenInteractiveActivity('{"tool_name":"write_file"}'), true);
  assert.equal(containsForbiddenInteractiveActivity("ordinary companion response"), false);
});

test("interactive exit preserves the desktop diagnostic text", async () => {
  const sent: Extract<ManagerHostMessage, { type: "interactive_cli_request" }>[] = [];
  const gateway = new InteractiveCliGateway("interactive-error-test", (message) => sent.push(message as Extract<ManagerHostMessage, { type: "interactive_cli_request" }>), 1000);
  const pending = gateway.request("start");
  const request = sent[0];
  gateway.handle({
    type: "interactive_cli_response",
    sessionId: "interactive-error-test",
    requestId: request.requestId,
    event: "exited",
    text: "交互式 Antigravity CLI 启动失败。",
    code: "AGY_INTERACTIVE_EXITED",
  });
  await assert.rejects(pending, (error: any) => {
    assert.equal(error.code, "AGY_INTERACTIVE_EXITED");
    assert.equal(error.message, "交互式 Antigravity CLI 启动失败。");
    return true;
  });
});

test("interactive image requests send the path only to the paste operation", async () => {
  const sessionId = "interactive-test";
  const requests: Extract<ManagerHostMessage, { type: "interactive_cli_request" }>[] = [];
  const streamed: string[] = [];
  let session: import("./antigravity-interactive.js").AntigravityInteractiveSession;
  const send = (message: ManagerHostMessage) => {
    if (message.type !== "interactive_cli_request") return;
    requests.push(message);
    const response = (event: "ready" | "output" | "paste_dispatched" | "input_written" | "submitted" | "clipboard_released", text?: string) =>
      session.handleResponse({ type: "interactive_cli_response", sessionId, requestId: message.requestId, event, text, conversationId: "conversation-1" });
    if (message.operation === "start") response("ready");
    else if (message.operation === "paste_image") response("paste_dispatched");
    else if (message.operation === "write_text") response("input_written");
    else if (message.operation === "submit") {
      session.handleResponse({ type: "interactive_cli_response", sessionId, requestId: message.requestId, event: "output", text: '{"schemaVersion":1,"action":"respond","message":"看到了"}' });
      response("submitted");
    } else if (message.operation === "release_clipboard") response("clipboard_released");
  };
  session = new (await import("./antigravity-interactive.js")).AntigravityInteractiveSession({
    executable: "agy", runtime: { root: "C:\\manager-runtime", schemaPath: "C:\\schema.json", logPath: "C:\\manager.log", agentName: "ilmatto-manager-test", agentPath: "C:\\agent.md" },
    timeoutSeconds: 10, effort: "medium", sessionId, send,
  });
  const turn = await session.ask("请看这张图", (event) => streamed.push(event.text), [{ type: "image", path: "C:\\managed\\image.png" }]);
  assert.equal(turn.action.message, "看到了");
  assert.equal(requests.find((request) => request.operation === "paste_image")?.attachmentPath, "C:\\managed\\image.png");
  assert.equal(requests.find((request) => request.operation === "write_text")?.attachmentPath, undefined);
  assert.equal(streamed.join(""), "看到了");
});
