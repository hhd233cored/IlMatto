import assert from "node:assert/strict";
import { mkdir, mkdtemp, rm, writeFile } from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import { test } from "node:test";
import { deleteAntigravitySdkSession, findPythonExecutable, sdkSessionRoot } from "./antigravity-sdk.js";

test("SDK session paths stay inside the configured IlMatto directory", async () => {
  const root = await mkdtemp(path.join(os.tmpdir(), "ilmatto-sdk-session-"));
  const previous = process.env.ILMATTO_ANTIGRAVITY_SDK_SESSION_DIR;
  process.env.ILMATTO_ANTIGRAVITY_SDK_SESSION_DIR = root;
  const sessionRef = "sdk-abcdefghijklmnopqrstuvwxyz123456";
  try {
    const sessionDirectory = path.join(root, sessionRef);
    await mkdir(sessionDirectory, { recursive: true });
    await writeFile(path.join(sessionDirectory, "state.json"), "{}", "utf8");
    await deleteAntigravitySdkSession(sessionRef);
    await assert.rejects(() => writeFile(path.join(sessionDirectory, "state.json"), "{}", "utf8"));
    assert.equal(sdkSessionRoot(), path.resolve(root));
  } finally {
    if (previous === undefined) delete process.env.ILMATTO_ANTIGRAVITY_SDK_SESSION_DIR;
    else process.env.ILMATTO_ANTIGRAVITY_SDK_SESSION_DIR = previous;
    await rm(root, { recursive: true, force: true });
  }
});

test("SDK Python override takes precedence over packaged runtime", () => {
  const previous = process.env.ILMATTO_ANTIGRAVITY_PYTHON;
  process.env.ILMATTO_ANTIGRAVITY_PYTHON = "C:\\Tools\\python.exe";
  try { assert.equal(findPythonExecutable("C:\\missing\\bridge.py"), "C:\\Tools\\python.exe"); }
  finally {
    if (previous === undefined) delete process.env.ILMATTO_ANTIGRAVITY_PYTHON;
    else process.env.ILMATTO_ANTIGRAVITY_PYTHON = previous;
  }
});
