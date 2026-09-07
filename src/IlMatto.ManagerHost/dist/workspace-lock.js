import path from "node:path";
export class WorkspaceLockError extends Error {
    code;
    constructor(code, message) {
        super(message);
        this.code = code;
        this.name = "WorkspaceLockError";
    }
}
/**
 * Coordinates task slots and workspace write ownership for every Manager
 * session in one ManagerHost process. Read leases are intentionally allowed
 * alongside a write lease: the caller must still place the Antigravity turn
 * in explicit read-only mode. Write leases are exclusive per canonical path.
 */
export class WorkspaceLockManager {
    leases = new Map();
    writers = new Map();
    maxConcurrentTasks;
    constructor(maxConcurrentTasks = readConcurrencyLimit()) {
        this.maxConcurrentTasks = Math.max(1, Math.min(16, Math.trunc(maxConcurrentTasks) || 1));
    }
    get limit() { return this.maxConcurrentTasks; }
    get activeCount() { return this.leases.size; }
    acquire(workspacePath, sessionId, taskId, provider, mode) {
        const normalizedPath = normalizeWorkspacePath(workspacePath);
        if (this.leases.has(taskId))
            throw new WorkspaceLockError("WORKSPACE_BUSY", `任务 ${taskId} 已经持有工作区执行锁。`);
        if (this.leases.size >= this.maxConcurrentTasks)
            throw new WorkspaceLockError("CONCURRENCY_LIMIT", `当前已达到并发任务上限（${this.maxConcurrentTasks}）。`);
        if (mode === "write") {
            const holder = this.writers.get(normalizedPath);
            if (holder && holder.taskId !== taskId)
                throw new WorkspaceLockError("WORKSPACE_BUSY", `工作区正由 ${holder.provider === "codex" ? "Codex" : "Antigravity"} 任务 ${holder.taskId} 使用。`);
        }
        const lease = { workspacePath: normalizedPath, sessionId, taskId, provider, mode };
        this.leases.set(taskId, lease);
        if (mode === "write")
            this.writers.set(normalizedPath, lease);
        return lease;
    }
    release(taskId) {
        const lease = this.leases.get(taskId);
        if (!lease)
            return;
        this.leases.delete(taskId);
        if (lease.mode === "write" && this.writers.get(lease.workspacePath)?.taskId === taskId)
            this.writers.delete(lease.workspacePath);
    }
    releaseSession(sessionId) {
        for (const lease of [...this.leases.values()])
            if (lease.sessionId === sessionId)
                this.release(lease.taskId);
    }
    releaseAll() {
        this.leases.clear();
        this.writers.clear();
    }
    getWriter(workspacePath) { return this.writers.get(normalizeWorkspacePath(workspacePath)); }
    has(taskId) { return this.leases.has(taskId); }
}
export function normalizeWorkspacePath(workspacePath) {
    const resolved = path.resolve(workspacePath.trim());
    return process.platform === "win32" ? resolved.toLowerCase() : resolved;
}
function readConcurrencyLimit() {
    const raw = Number.parseInt(process.env.ILMATTO_MAX_CONCURRENT_TASKS ?? "4", 10);
    return Number.isFinite(raw) ? raw : 4;
}
//# sourceMappingURL=workspace-lock.js.map