import assert from "node:assert/strict";
import { execFile } from "node:child_process";
import fs from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import { promisify } from "node:util";
import { test } from "node:test";
import { commitGitChanges, createGitBranch, createInitialGitCommit, ensureRepository, getGitDiff, getGitOverview, initGitRepository, stageGitFiles, switchGitBranch, unstageGitFiles } from "./git-service.js";
import { isGitWriteTool } from "./approval-policy.js";
import { applyPatch, readFile, runCommand } from "./tools.js";

const execFileAsync = promisify(execFile);

async function git(cwd: string, ...args: string[]): Promise<void> {
  await execFileAsync("git", args, { cwd, windowsHide: true });
}

async function createRepository(): Promise<string> {
  const workspace = await fs.mkdtemp(path.join(os.tmpdir(), "ilmatto-git-"));
  await git(workspace, "init", "-b", "main");
  await git(workspace, "config", "user.name", "IlMatto Test");
  await git(workspace, "config", "user.email", "test@example.invalid");
  await fs.writeFile(path.join(workspace, "README.md"), "initial\n", "utf8");
  await git(workspace, "add", "--", "README.md");
  await git(workspace, "commit", "--no-verify", "-m", "initial");
  return workspace;
}

test("Git can be initialized in a plain workspace and then inspected", async () => {
  const workspace = await fs.mkdtemp(path.join(os.tmpdir(), "ilmatto-git-init-"));
  await fs.writeFile(path.join(workspace, "notes.txt"), "untracked\n", "utf8");

  assert.match(await initGitRepository(workspace), /created initial commit/i);
  const context = await ensureRepository(workspace);
  assert.equal(context.repositoryRoot, workspace);
  const overview = await getGitOverview(workspace);
  assert.equal(overview.isRepository, true);
  assert.deepEqual(overview.untracked, []);
  assert.equal(overview.commits[0]?.subject, "Initial commit");
  await assert.rejects(() => initGitRepository(workspace), /already inside a Git repository/i);
});

test("Git can add the initial commit to an existing empty repository", async () => {
  const workspace = await fs.mkdtemp(path.join(os.tmpdir(), "ilmatto-git-empty-"));
  await git(workspace, "init", "-b", "main");
  await fs.writeFile(path.join(workspace, "README.md"), "initial\n", "utf8");

  assert.match(await createInitialGitCommit(workspace), /Created initial commit/);
  const overview = await getGitOverview(workspace);
  assert.equal(overview.commits[0]?.subject, "Initial commit");
  assert.deepEqual(overview.untracked, []);
});

test("Git discovers an ancestor repository and scopes file changes to the selected workspace", async () => {
  const repository = await createRepository();
  const nested = path.join(repository, "src");
  await fs.mkdir(nested);
  await fs.writeFile(path.join(nested, "inside.txt"), "inside\n", "utf8");
  await fs.writeFile(path.join(repository, "outside.txt"), "outside\n", "utf8");

  const context = await ensureRepository(nested);
  assert.equal(context.repositoryRoot, repository);
  assert.equal(context.workspaceRoot, nested);
  assert.equal(context.isRepositoryRoot, false);

  const overview = await getGitOverview(nested);
  assert.equal(overview.isRepository, true);
  assert.deepEqual(overview.untracked.map((item) => item.path), ["inside.txt"]);
  await stageGitFiles(nested, ["inside.txt"]);
  assert.match((await getGitDiff(nested, "staged", "inside.txt")).content, /inside/);
  await assert.rejects(() => createGitBranch(nested, "feature/subtree"), /repository root/i);
  await git(repository, "add", "--", "outside.txt");
  await assert.rejects(() => commitGitChanges(nested, "must not include outside"), /outside the selected workspace/i);
});

test("controlled Git service stages, unstages, commits, and switches local branches from repository root", async () => {
  const workspace = await createRepository();
  await fs.writeFile(path.join(workspace, "README.md"), "changed\n", "utf8");

  await stageGitFiles(workspace, ["README.md"]);
  let overview = await getGitOverview(workspace);
  assert.equal(overview.staged.length, 1);
  assert.match((await getGitDiff(workspace, "staged", "README.md")).content, /changed/);

  await unstageGitFiles(workspace, ["README.md"]);
  overview = await getGitOverview(workspace);
  assert.equal(overview.staged.length, 0);
  assert.equal(overview.unstaged.length, 1);
  await assert.rejects(() => commitGitChanges(workspace, "should fail"), /no staged changes/i);

  await stageGitFiles(workspace, ["README.md"]);
  assert.match(await commitGitChanges(workspace, "update readme"), /Created commit/);
  overview = await getGitOverview(workspace);
  assert.equal(overview.commits[0]?.subject, "update readme");

  await createGitBranch(workspace, "feature/git-panel");
  assert.equal((await getGitOverview(workspace)).branch, "feature/git-panel");
  await switchGitBranch(workspace, "main");
  assert.equal((await getGitOverview(workspace)).branch, "main");
});

test("file and PowerShell tools cannot bypass controlled Git boundaries", async () => {
  const workspace = await createRepository();
  await assert.rejects(() => readFile(workspace, ".git/config"), /Git metadata/);
  await assert.rejects(() => applyPatch(workspace, ".git/config", "blocked", async () => true), /Git metadata/);
  await assert.rejects(() => stageGitFiles(workspace, ["..\\outside.txt"]), /outside/);
  await assert.rejects(() => runCommand(workspace, "git status", async () => true), /dedicated Git tools/);
  assert.equal(isGitWriteTool("git_commit"), true);
  assert.equal(isGitWriteTool("git_diff"), false);
});
