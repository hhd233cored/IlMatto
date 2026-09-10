import test from "node:test";
import assert from "node:assert/strict";
import { WorkspaceLockError, WorkspaceLockManager } from "./workspace-lock.js";
test("workspace writes are exclusive while reads can observe a writer", () => {
    const locks = new WorkspaceLockManager(4);
    const writer = locks.acquire("C:\\Repo", "a", "task-a", "antigravity", "write");
    const reader = locks.acquire("c:\\repo\\.", "b", "turn-b", "antigravity", "read");
    assert.equal(reader.mode, "read");
    assert.throws(() => locks.acquire("C:\\repo", "c", "task-c", "antigravity", "write"), (error) => error instanceof WorkspaceLockError && error.code === "WORKSPACE_BUSY");
    locks.release(writer.taskId);
    const nextWriter = locks.acquire("C:\\repo", "c", "task-c", "antigravity", "write");
    assert.equal(nextWriter.mode, "write");
});
test("concurrency limit is applied across sessions", () => {
    const locks = new WorkspaceLockManager(2);
    locks.acquire("C:\\a", "a", "task-a", "antigravity", "write");
    locks.acquire("C:\\b", "b", "task-b", "antigravity", "read");
    assert.throws(() => locks.acquire("C:\\c", "c", "task-c", "antigravity", "read"), (error) => error instanceof WorkspaceLockError && error.code === "CONCURRENCY_LIMIT");
});
//# sourceMappingURL=workspace-lock.test.js.map