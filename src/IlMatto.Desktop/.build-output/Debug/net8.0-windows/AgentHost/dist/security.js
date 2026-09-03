import path from "node:path";
import fs from "node:fs/promises";
export function normalizeWorkspace(workspacePath) {
    const resolved = path.resolve(workspacePath);
    if (!path.isAbsolute(resolved))
        throw new Error("Workspace path must be absolute");
    return resolved;
}
export function resolveInsideWorkspace(workspacePath, requestedPath) {
    const root = normalizeWorkspace(workspacePath);
    const candidate = path.resolve(root, requestedPath);
    const relative = path.relative(root, candidate);
    if (relative === "" || (!relative.startsWith(".." + path.sep) && relative !== ".." && !path.isAbsolute(relative)))
        return candidate;
    throw new Error("Path is outside the selected workspace");
}
export function assertNotGitMetadataPath(workspacePath, requestedPath) {
    const root = normalizeWorkspace(workspacePath);
    const candidate = resolveInsideWorkspace(root, requestedPath);
    const relative = path.relative(root, candidate);
    const firstSegment = relative.split(path.sep, 1)[0]?.toLowerCase();
    if (firstSegment === ".git")
        throw new Error("Git metadata is not accessible through file tools");
    return candidate;
}
export async function assertNoSymlinkEscape(workspacePath, candidatePath) {
    const root = normalizeWorkspace(workspacePath);
    const existing = await findExistingParent(candidatePath);
    const realRoot = await fs.realpath(root);
    const realExisting = await fs.realpath(existing);
    const relative = path.relative(realRoot, realExisting);
    if (relative.startsWith(".." + path.sep) || relative === ".." || path.isAbsolute(relative))
        throw new Error("Symlink escapes workspace");
}
async function findExistingParent(candidatePath) {
    let current = candidatePath;
    while (true) {
        try {
            await fs.lstat(current);
            return current;
        }
        catch {
            const parent = path.dirname(current);
            if (parent === current)
                throw new Error("Unable to resolve path");
            current = parent;
        }
    }
}
//# sourceMappingURL=security.js.map