import assert from "node:assert/strict";
import { test } from "node:test";
import { ExecutionLease, defaultExecutorFor, parseExecutorDirective, redactAndLimit } from "./task-orchestrator.js";
test("executor directives apply only at the start of a task", () => {
    assert.deepEqual(parseExecutorDirective(" @codex 修复测试"), { executor: "codex", request: "修复测试" });
    assert.deepEqual(parseExecutorDirective("请问 Codex 能否修复测试"), { request: "请问 Codex 能否修复测试" });
    assert.equal(defaultExecutorFor("antigravity"), "antigravity");
    assert.equal(defaultExecutorFor("openai_compatible"), "pi");
});
test("execution lease excludes concurrent workspace work", () => {
    const lease = new ExecutionLease();
    lease.acquire("one", "implementing");
    assert.throws(() => lease.acquire("two", "verifying"), /工作区/);
    lease.release("one");
    lease.acquire("two", "verifying");
    assert.equal(lease.active?.taskId, "two");
});
test("private trace text is redacted and bounded", () => {
    assert.match(redactAndLimit("Bearer abcdefghijklmnop"), /已脱敏/);
    assert.match(redactAndLimit("x".repeat(70_000)), /输出已截断/);
});
//# sourceMappingURL=task-orchestrator.test.js.map