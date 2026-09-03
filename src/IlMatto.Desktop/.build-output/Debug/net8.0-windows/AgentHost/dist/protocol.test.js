import assert from "node:assert/strict";
import { test } from "node:test";
import { isClientMessage } from "./protocol.js";
test("protocol accepts known client messages", () => {
    assert.equal(isClientMessage({ type: "send_message", sessionId: "s", text: "hello" }), true);
    assert.equal(isClientMessage({ type: "approve_tool_call", sessionId: "s", callId: "c", approved: false }), true);
    assert.equal(isClientMessage({ type: "get_commands", sessionId: "s" }), true);
    assert.equal(isClientMessage({ type: "get_git_overview", sessionId: "s" }), true);
    assert.equal(isClientMessage({ type: "get_git_diff", sessionId: "s", scope: "staged", path: "README.md" }), true);
    assert.equal(isClientMessage({ type: "unknown" }), false);
    assert.equal(isClientMessage(null), false);
});
//# sourceMappingURL=protocol.test.js.map