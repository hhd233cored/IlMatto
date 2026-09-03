import { execFile } from "node:child_process";
import fs from "node:fs/promises";
import path from "node:path";
import { promisify } from "node:util";
import { assertNoSymlinkEscape, assertNotGitMetadataPath, normalizeWorkspace } from "./security.js";
const execFileAsync = promisify(execFile);
const MAX_OUTPUT = 1024 * 1024;
export async function getGitOverview(workspacePath) {
    try {
        const repository = await ensureRepository(workspacePath);
        const [status, branch, upstream, branches, commits, remotes] = await Promise.all([
            gitOutput(repository.repositoryRoot, ["status", "--porcelain=v1", "--branch", "--untracked-files=all", ...scopeArgs(repository)]),
            gitOutput(repository.repositoryRoot, ["branch", "--show-current"]),
            optionalGitOutput(repository.repositoryRoot, ["rev-parse", "--abbrev-ref", "@{upstream}"]),
            gitOutput(repository.repositoryRoot, ["for-each-ref", "--format=%(HEAD)%x1f%(refname:short)%x1f%(upstream:short)", "refs/heads"]),
            optionalGitOutput(repository.repositoryRoot, ["log", "-25", "--format=%H%x1f%h%x1f%s%x1f%an%x1f%aI%x1e"]),
            gitOutput(repository.repositoryRoot, ["remote", "-v"]),
        ]);
        const parsedStatus = parseStatus(status, repository);
        const parsedUpstream = upstream.trim() || undefined;
        const [ahead, behind] = parsedUpstream ? await getAheadBehind(repository.repositoryRoot) : [0, 0];
        return {
            isRepository: true,
            root: repository.repositoryRoot,
            branch: branch.trim() || "(detached HEAD)",
            upstream: parsedUpstream,
            ahead,
            behind,
            ...parsedStatus,
            branches: parseBranches(branches),
            commits: parseCommits(commits),
            remotes: parseRemotes(remotes),
        };
    }
    catch (error) {
        return emptyOverview(error instanceof Error ? error.message : "Unable to inspect Git repository");
    }
}
export async function initGitRepository(workspacePath) {
    const workspace = normalizeWorkspace(workspacePath);
    const realWorkspace = await fs.realpath(workspace);
    const stat = await fs.stat(realWorkspace);
    if (!stat.isDirectory())
        throw new Error("The selected workspace must be a directory");
    try {
        const existingRoot = (await gitOutput(realWorkspace, ["rev-parse", "--show-toplevel"])).trim();
        if (existingRoot)
            throw new Error(`The workspace is already inside a Git repository at ${existingRoot}`);
    }
    catch (error) {
        const message = error instanceof Error ? error.message : String(error);
        const normalized = message.toLowerCase();
        if (!normalized.includes("not a git repository") && !normalized.includes("not a git repo") && !normalized.includes("outside repository"))
            throw error;
    }
    await gitOutput(realWorkspace, ["init"]);
    return createInitialGitCommit(realWorkspace, true);
}
export async function createInitialGitCommit(workspacePath, repositoryAlreadyInitialized = false) {
    const repository = await ensureRepository(workspacePath);
    assertRepositoryWideOperation(repository, "Creating an initial commit");
    if (!repositoryAlreadyInitialized && (await optionalGitOutput(repository.repositoryRoot, ["rev-parse", "--verify", "HEAD"])).trim()) {
        throw new Error("The Git repository already has a commit");
    }
    await gitOutput(repository.repositoryRoot, ["add", "--all"]);
    const stagedFiles = (await gitOutput(repository.repositoryRoot, ["diff", "--cached", "--name-only", "-z", "--no-ext-diff"])).split("\0").filter(Boolean);
    if (stagedFiles.length === 0)
        return `Git repository is ready at ${repository.repositoryRoot}; there were no files to commit.`;
    await gitOutput(repository.repositoryRoot, ["commit", "--no-verify", "-m", "Initial commit"]);
    return `Created initial commit (${stagedFiles.length} file${stagedFiles.length === 1 ? "" : "s"}) in ${repository.repositoryRoot}.`;
}
export async function getGitDiff(workspacePath, scope, relativePath) {
    const repository = await ensureRepository(workspacePath);
    const pathspecs = relativePath ? [await toGitPath(repository, relativePath)] : scopeArgs(repository);
    const args = scope === "staged" ? ["diff", "--cached", "--no-ext-diff"] : ["diff", "--no-ext-diff"];
    if (pathspecs.length)
        args.push("--", ...pathspecs);
    const content = await gitOutput(repository.repositoryRoot, args);
    return truncateDiff(scope, relativePath, content);
}
export async function getGitLog(workspacePath, limit = 25) {
    const repository = await ensureRepository(workspacePath);
    return optionalGitOutput(repository.repositoryRoot, ["log", `-${Math.min(Math.max(limit, 1), 100)}`, "--oneline", "--decorate", "--no-ext-diff"]);
}
export async function getGitBranches(workspacePath) {
    const repository = await ensureRepository(workspacePath);
    return gitOutput(repository.repositoryRoot, ["branch", "--verbose", "--no-abbrev"]);
}
export async function getGitRemotes(workspacePath) {
    const repository = await ensureRepository(workspacePath);
    return gitOutput(repository.repositoryRoot, ["remote", "-v"]);
}
export async function showGitCommit(workspacePath, revision) {
    const repository = await ensureRepository(workspacePath);
    if (!revision || revision.startsWith("-"))
        throw new Error("Invalid Git revision");
    await gitOutput(repository.repositoryRoot, ["rev-parse", "--verify", `${revision}^{commit}`]);
    return gitOutput(repository.repositoryRoot, ["show", "--no-ext-diff", "--stat", "--format=fuller", revision, ...scopeArgs(repository)]);
}
export async function stageGitFiles(workspacePath, paths) {
    const repository = await ensureRepository(workspacePath);
    const pathspecs = await toGitPaths(repository, paths);
    await gitOutput(repository.repositoryRoot, ["add", "--", ...pathspecs]);
    return `Staged ${pathspecs.join(", ")}.`;
}
export async function unstageGitFiles(workspacePath, paths) {
    const repository = await ensureRepository(workspacePath);
    const pathspecs = await toGitPaths(repository, paths);
    await gitOutput(repository.repositoryRoot, ["restore", "--staged", "--", ...pathspecs]);
    return `Unstaged ${pathspecs.join(", ")}.`;
}
export async function createGitBranch(workspacePath, name) {
    const repository = await ensureRepository(workspacePath);
    assertRepositoryWideOperation(repository, "Creating a branch");
    await validateBranchName(repository.repositoryRoot, name);
    await gitOutput(repository.repositoryRoot, ["switch", "-c", name]);
    return `Created and switched to branch ${name}.`;
}
export async function switchGitBranch(workspacePath, name) {
    const repository = await ensureRepository(workspacePath);
    assertRepositoryWideOperation(repository, "Switching a branch");
    await validateBranchName(repository.repositoryRoot, name);
    await gitOutput(repository.repositoryRoot, ["switch", name]);
    return `Switched to branch ${name}.`;
}
export async function commitGitChanges(workspacePath, message) {
    const repository = await ensureRepository(workspacePath);
    const normalizedMessage = message.trim();
    if (!normalizedMessage || normalizedMessage.length > 4096 || normalizedMessage.includes("\0"))
        throw new Error("Commit message must contain 1 to 4096 characters");
    const allStagedPaths = (await gitOutput(repository.repositoryRoot, ["diff", "--cached", "--name-only", "-z", "--no-ext-diff"])).split("\0").filter(Boolean);
    if (allStagedPaths.length === 0)
        throw new Error("There are no staged changes to commit");
    if (allStagedPaths.some((stagedPath) => !isInsideWorkspace(repository, stagedPath))) {
        throw new Error("There are staged changes outside the selected workspace. Select the repository root to commit them.");
    }
    await gitOutput(repository.repositoryRoot, ["commit", "--no-verify", "-m", normalizedMessage]);
    return `Created commit: ${normalizedMessage}`;
}
export async function getStagedSummary(workspacePath) {
    const repository = await ensureRepository(workspacePath);
    const output = await gitOutput(repository.repositoryRoot, ["diff", "--cached", "--name-status", "--no-ext-diff", ...scopeArgs(repository)]);
    return output.trim() || "No staged files.";
}
export async function ensureRepository(workspacePath) {
    const workspace = normalizeWorkspace(workspacePath);
    let root;
    try {
        root = (await gitOutput(workspace, ["rev-parse", "--show-toplevel"])).trim();
    }
    catch (error) {
        const message = error instanceof Error ? error.message : String(error);
        const normalized = message.toLowerCase();
        if (normalized.includes("not a git repository") || normalized.includes("not a git repo") || normalized.includes("outside repository")) {
            throw new Error("The selected workspace is not inside a Git repository");
        }
        throw error;
    }
    const [realWorkspace, realRoot] = await Promise.all([fs.realpath(workspace), fs.realpath(root)]);
    const workspaceRelative = path.relative(realRoot, realWorkspace);
    if (workspaceRelative === ".." || workspaceRelative.startsWith(`..${path.sep}`) || path.isAbsolute(workspaceRelative))
        throw new Error("Workspace is outside the discovered Git repository");
    return {
        workspaceRoot: realWorkspace,
        repositoryRoot: realRoot,
        workspacePathspec: workspaceRelative ? workspaceRelative.split(path.sep).join("/") : undefined,
        isRepositoryRoot: !workspaceRelative,
    };
}
async function gitOutput(workspace, args) {
    try {
        const result = await execFileAsync("git", args, { cwd: workspace, timeout: 30_000, maxBuffer: MAX_OUTPUT, windowsHide: true });
        return `${result.stdout}${result.stderr ? `\n${result.stderr}` : ""}`.trimEnd();
    }
    catch (error) {
        const details = `${error?.stdout ?? ""}${error?.stderr ? `\n${error.stderr}` : ""}`.trim();
        throw new Error(details || error?.message || "Git command failed");
    }
}
async function optionalGitOutput(workspace, args) {
    try {
        return await gitOutput(workspace, args);
    }
    catch {
        return "";
    }
}
function scopeArgs(repository) {
    return repository.workspacePathspec ? ["--", repository.workspacePathspec] : [];
}
function assertRepositoryWideOperation(repository, operation) {
    if (!repository.isRepositoryRoot)
        throw new Error(`${operation} can affect files outside the selected workspace. Select the repository root to continue.`);
}
function isInsideWorkspace(repository, repositoryRelativePath) {
    const candidate = path.resolve(repository.repositoryRoot, repositoryRelativePath);
    const relative = path.relative(repository.workspaceRoot, candidate);
    return relative !== ".." && !relative.startsWith(`..${path.sep}`) && !path.isAbsolute(relative);
}
async function getAheadBehind(workspace) {
    const output = await optionalGitOutput(workspace, ["rev-list", "--left-right", "--count", "HEAD...@{upstream}"]);
    const [ahead = "0", behind = "0"] = output.trim().split(/\s+/);
    return [Number(ahead) || 0, Number(behind) || 0];
}
async function toGitPaths(repository, paths) {
    if (!Array.isArray(paths) || paths.length === 0)
        throw new Error("At least one repository-relative path is required");
    return Promise.all(paths.map((relativePath) => toGitPath(repository, relativePath)));
}
async function toGitPath(repository, relativePath) {
    if (!relativePath || path.isAbsolute(relativePath))
        throw new Error("Git paths must be relative to the workspace");
    const candidate = assertNotGitMetadataPath(repository.workspaceRoot, relativePath);
    await assertNoSymlinkEscape(repository.workspaceRoot, candidate);
    const relative = path.relative(repository.workspaceRoot, candidate);
    if (!relative || relative === ".")
        throw new Error("Git paths must name a file or directory inside the workspace");
    return path.relative(repository.repositoryRoot, candidate).split(path.sep).join("/");
}
async function validateBranchName(workspace, name) {
    const normalized = name.trim();
    if (!normalized || normalized.startsWith("-"))
        throw new Error("Invalid branch name");
    await gitOutput(workspace, ["check-ref-format", "--branch", normalized]);
}
function parseStatus(value, repository) {
    const staged = [];
    const unstaged = [];
    const untracked = [];
    for (const line of value.split(/\r?\n/)) {
        if (!line || line.startsWith("##"))
            continue;
        const x = line[0] ?? " ";
        const y = line[1] ?? " ";
        const repositoryPath = line.slice(3).trim();
        const file = path.relative(repository.workspaceRoot, path.resolve(repository.repositoryRoot, repositoryPath)).split(path.sep).join("/");
        if (!file)
            continue;
        if (x === "?" && y === "?") {
            untracked.push({ path: file, status: "??", kind: "untracked" });
            continue;
        }
        if (x !== " ")
            staged.push({ path: file, status: x, kind: "staged" });
        if (y !== " ")
            unstaged.push({ path: file, status: y, kind: "unstaged" });
    }
    return { staged, unstaged, untracked };
}
function parseBranches(value) {
    return value.split(/\r?\n/).filter(Boolean).map((line) => {
        const [head, name, upstream] = line.split("\u001f");
        return { name: name ?? "", isCurrent: head === "*", upstream: upstream || undefined };
    }).filter((branch) => branch.name);
}
function parseCommits(value) {
    return value.split("\u001e").filter(Boolean).map((entry) => {
        const [id, shortId, subject, author, date] = entry.trim().split("\u001f");
        return { id: id ?? "", shortId: shortId ?? "", subject: subject ?? "", author: author ?? "", date: date ?? "" };
    }).filter((commit) => commit.id);
}
function parseRemotes(value) {
    const remotes = new Map();
    for (const line of value.split(/\r?\n/)) {
        const [name, url, direction] = line.split(/\s+/);
        if (!name || !url || !direction)
            continue;
        const remote = remotes.get(name) ?? { name };
        if (direction === "(fetch)")
            remote.fetchUrl = url;
        if (direction === "(push)")
            remote.pushUrl = url;
        remotes.set(name, remote);
    }
    return [...remotes.values()];
}
function truncateDiff(scope, relativePath, content) {
    if (content.length <= MAX_OUTPUT)
        return { scope, path: relativePath, content, truncated: false };
    return { scope, path: relativePath, content: `${content.slice(0, MAX_OUTPUT)}\n[diff truncated]`, truncated: true };
}
function emptyOverview(message) {
    return { isRepository: false, message, ahead: 0, behind: 0, staged: [], unstaged: [], untracked: [], branches: [], commits: [], remotes: [] };
}
//# sourceMappingURL=git-service.js.map