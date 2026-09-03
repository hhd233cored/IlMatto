import assert from "node:assert/strict";
import os from "node:os";
import path from "node:path";
import { mkdir, mkdtemp, readFile, readdir, rm, writeFile } from "node:fs/promises";
import { test } from "node:test";
import { buildImageAwareCompanionPrompt, stageManagedImages } from "./image-staging.js";

test("managed images are copied into the isolated runtime and only that path is put in the prompt", async () => {
  const root = await mkdtemp(path.join(os.tmpdir(), "ilmatto-image-staging-"));
  const previous = process.env.LOCALAPPDATA;
  process.env.LOCALAPPDATA = root;
  try {
    const sessionId = "session-1";
    const sourceDirectory = path.join(root, "IlMatto", "manager-sessions", "attachments", sessionId);
    const source = path.join(sourceDirectory, "user supplied name.png");
    await mkdir(sourceDirectory, { recursive: true });
    await writeFile(source, "fake png", "utf8");
    const runtime = {
      root: path.join(root, "runtime"),
      attachmentsRoot: path.join(root, "runtime", "attachments"),
      schemaPath: path.join(root, "schema.json"),
      logPath: path.join(root, "manager.log"),
      agentName: "ilmatto-manager-test",
      agentPath: path.join(root, "agent.md"),
    };
    const [staged] = await stageManagedImages(runtime, sessionId, [{
      type: "image", path: source, attachmentId: "attachment-1", displayName: "user supplied name.png", mimeType: "image/png", order: 0,
    }]);
    assert.equal(await readFile(staged.runtimePath, "utf8"), "fake png");
    assert.equal(path.basename(staged.runtimePath), "attachment-1.png");
    const prompt = buildImageAwareCompanionPrompt("这张图是什么？", [staged]);
    assert.match(prompt, /这张图是什么？/);
    assert.ok(prompt.includes(staged.runtimePath));
    assert.match(prompt, /only paths permitted for the read-only view_file tool/i);
    assert.match(prompt, /Pass one of the paths above exactly as written/i);
    assert.doesNotMatch(prompt, /user supplied name\.png/);
  } finally {
    if (previous === undefined) delete process.env.LOCALAPPDATA; else process.env.LOCALAPPDATA = previous;
    await rm(root, { recursive: true, force: true });
  }
});

test("managed image staging rejects a path outside the current session attachment directory", async () => {
  const root = await mkdtemp(path.join(os.tmpdir(), "ilmatto-image-staging-"));
  const previous = process.env.LOCALAPPDATA;
  process.env.LOCALAPPDATA = root;
  try {
    const sessionId = "session-2";
    const sourceDirectory = path.join(root, "IlMatto", "manager-sessions", "attachments", sessionId);
    await mkdir(sourceDirectory, { recursive: true });
    const outside = path.join(root, "outside.png");
    await writeFile(outside, "fake png", "utf8");
    const runtime = {
      root: path.join(root, "runtime"), attachmentsRoot: path.join(root, "runtime", "attachments"),
      schemaPath: "schema.json", logPath: "manager.log", agentName: "agent", agentPath: "agent.md",
    };
    await assert.rejects(
      stageManagedImages(runtime, sessionId, [{ type: "image", path: outside }]),
      /受管附件目录/,
    );
    const stagedFiles = await readdir(path.join(runtime.attachmentsRoot, sessionId)).catch(() => [] as string[]);
    assert.deepEqual(stagedFiles, []);
  } finally {
    if (previous === undefined) delete process.env.LOCALAPPDATA; else process.env.LOCALAPPDATA = previous;
    await rm(root, { recursive: true, force: true });
  }
});
