import assert from "node:assert/strict";
import fs from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import { test } from "node:test";
import { assertNoSymlinkEscape, resolveInsideWorkspace } from "./security.js";
import { applyPatch, listFiles, runCommand } from "./tools.js";
import { isSafeCommand } from "./approval-policy.js";

test("workspace path resolver rejects traversal", async () => {
  const workspace = await fs.mkdtemp(path.join(os.tmpdir(), "ilmatto-security-"));
  assert.equal(resolveInsideWorkspace(workspace, "src/main.ts"), path.join(workspace, "src", "main.ts"));
  assert.throws(() => resolveInsideWorkspace(workspace, "..\\outside.txt"), /outside/);
  await assertNoSymlinkEscape(workspace, workspace);
});

test("applyPatch waits for approval and writes only after approval", async () => {
  const workspace = await fs.mkdtemp(path.join(os.tmpdir(), "ilmatto-patch-"));
  await fs.writeFile(path.join(workspace, "hello.txt"), "before\n", "utf8");
  const denied = await applyPatch(workspace, "hello.txt", "after\n", async () => false);
  assert.match(denied, /denied/);
  assert.equal(await fs.readFile(path.join(workspace, "hello.txt"), "utf8"), "before\n");
  const approved = await applyPatch(workspace, "hello.txt", "after\n", async (request) => request.diff?.includes("-before") === true);
  assert.match(approved, /Applied/);
  assert.equal(await fs.readFile(path.join(workspace, "hello.txt"), "utf8"), "after\n");
});

test("applyPatch writes after an asynchronous approval response", async () => {
  const workspace = await fs.mkdtemp(path.join(os.tmpdir(), "ilmatto-patch-await-"));
  let release!: (approved: boolean) => void;
  let approvalStarted!: () => void;
  const started = new Promise<void>((resolve) => { approvalStarted = resolve; });
  const pending = applyPatch(workspace, "changed.txt", "approved\n", () => {
    approvalStarted();
    return new Promise<boolean>((resolve) => { release = resolve; });
  });
  await started;
  assert.equal(await fs.access(path.join(workspace, "changed.txt")).then(() => true).catch(() => false), false);
  release(true);
  assert.match(await pending, /Applied/);
  assert.equal(await fs.readFile(path.join(workspace, "changed.txt"), "utf8"), "approved\n");
});

test("listFiles hides common generated directories", async () => {
  const workspace = await fs.mkdtemp(path.join(os.tmpdir(), "ilmatto-list-"));
  await fs.mkdir(path.join(workspace, "node_modules"));
  await fs.writeFile(path.join(workspace, "README.md"), "# test", "utf8");
  const output = await listFiles(workspace);
  assert.match(output, /README\.md/);
  assert.doesNotMatch(output, /node_modules/);
});

test("runCommand does not execute when approval is denied", async () => {
  const workspace = await fs.mkdtemp(path.join(os.tmpdir(), "ilmatto-command-"));
  const output = await runCommand(workspace, "Write-Output should-not-run", async () => false);
  assert.match(output, /denied/);
});

test("safe command policy only auto-approves read-only and validation commands", () => {
  assert.equal(isSafeCommand("Get-Content README.md"), true);
  assert.equal(isSafeCommand("dotnet test"), true);
  assert.equal(isSafeCommand("Get-Content README.md; Remove-Item README.md"), false);
  assert.equal(isSafeCommand("git reset --hard HEAD"), false);
});
