import assert from "node:assert/strict";
import { test } from "node:test";
import os from "node:os";
import path from "node:path";
import { mkdtemp, rm } from "node:fs/promises";
import { BrowserController } from "./browser-controller.js";

test("BrowserController does not start Chrome until a browser request arrives", () => {
  const events: unknown[] = [];
  const controller = new BrowserController((event) => events.push(event));
  assert.equal(controller.currentState, "Stopped");
  assert.equal(events.length, 0);
});

test("BrowserController reports a structured error when Chrome is unavailable", async () => {
  const previous = process.env.ILMATTO_BROWSER_CHROME_PATH;
  const previousProfile = process.env.ILMATTO_BROWSER_PROFILE;
  const profile = await mkdtemp(path.join(os.tmpdir(), "ilmatto-browser-controller-"));
  process.env.ILMATTO_BROWSER_CHROME_PATH = "C:\\path\\that\\does-not-exist\\chrome.exe";
  process.env.ILMATTO_BROWSER_PROFILE = profile;
  const events: any[] = [];
  const controller = new BrowserController((event) => events.push(event));
  try {
    await assert.rejects(
      controller.handle("session-a", { type: "browser_request", sessionId: "session-a", requestId: "r1", operation: "start" }),
      (error: any) => error?.code === "BROWSER_CHROME_NOT_FOUND",
    );
    assert.equal(controller.currentState, "Error");
    assert.equal(events.some((event) => event.type === "browser_state" && event.state === "Starting"), true);
    assert.equal(events.some((event) => event.type === "browser_state" && event.state === "Error"), true);
  } finally {
    if (previous === undefined) delete process.env.ILMATTO_BROWSER_CHROME_PATH;
    else process.env.ILMATTO_BROWSER_CHROME_PATH = previous;
    if (previousProfile === undefined) delete process.env.ILMATTO_BROWSER_PROFILE;
    else process.env.ILMATTO_BROWSER_PROFILE = previousProfile;
    await rm(profile, { recursive: true, force: true });
    await controller.dispose();
  }
});

test("BrowserController detects human verification and locks writes", async () => {
  const events: any[] = [];
  const controller = new BrowserController((event) => events.push(event));
  (controller as any).ownerSessionId = "session-a";
  (controller as any).state = "AgentControlled";
  const result = await controller.handle("session-a", {
    type: "browser_request", sessionId: "session-a", requestId: "r2", operation: "page_state",
    page: { url: "https://example.test/login", title: "验证码", text: "请完成验证码" },
  });
  assert.equal(result.state, "WaitingForHuman");
  await assert.rejects(
    controller.handle("session-a", { type: "browser_request", sessionId: "session-a", requestId: "r3", operation: "click", ref: "e1" }),
    (error: any) => error?.code === "BROWSER_CONTROLLED_BY_HUMAN",
  );
  assert.equal(events.at(-1)?.state, "WaitingForHuman");
  await controller.dispose();
});

test("BrowserController keeps the headed browser visible after human verification", async () => {
  const controller = new BrowserController(() => undefined);
  (controller as any).ownerSessionId = "session-a";
  (controller as any).state = "WaitingForHuman";
  (controller as any).visible = true;
  const result = await controller.humanDone("session-a");
  assert.equal(result.state, "AgentControlled");
  assert.equal(result.visible, true);
  await controller.dispose();
});

test("BrowserController resumes agent writes after a clean snapshot", async () => {
  const events: any[] = [];
  const controller = new BrowserController((event) => events.push(event));
  (controller as any).ownerSessionId = "session-a";
  (controller as any).state = "WaitingForHuman";
  (controller as any).visible = true;
  const result = await controller.handle("session-a", {
    type: "browser_request", sessionId: "session-a", requestId: "r-clean", operation: "page_state",
    page: { url: "https://example.test/home", title: "Example", text: "Welcome back" },
  });
  assert.equal(result.state, "AgentControlled");
  assert.equal(result.visible, true);
  assert.match(events.at(-1)?.message ?? "", /自动恢复 Agent/);
  await controller.dispose();
});

test("BrowserController retries startup when the desktop opens a browser in Error state", async () => {
  const controller = new BrowserController(() => undefined);
  (controller as any).ownerSessionId = "session-a";
  (controller as any).state = "Error";
  let starts = 0;
  (controller as any).start = async () => {
    starts += 1;
    (controller as any).state = "AgentControlled";
    return (controller as any).snapshotState("session-a");
  };
  const result = await controller.setVisibility("session-a", true);
  assert.equal(starts, 1);
  assert.equal(result.state, "AgentControlled");
  assert.equal(result.visible, true);
  await controller.dispose();
});

test("BrowserController enforces per-session permissions without an approval round trip", async () => {
  const controller = new BrowserController(() => undefined);
  (controller as any).ownerSessionId = "session-a";
  (controller as any).state = "AgentControlled";
  controller.configureSession("session-a", { click: false, evaluate: true });
  await assert.rejects(
    controller.handle("session-a", {
      type: "browser_request", sessionId: "session-a", requestId: "r4", operation: "authorize",
      permission: "click", write: true,
    }),
    (error: any) => error?.code === "BROWSER_PERMISSION_DENIED",
  );
  const allowed = await controller.handle("session-a", {
    type: "browser_request", sessionId: "session-a", requestId: "r5", operation: "authorize",
    permission: "evaluate", write: true,
  });
  assert.deepEqual(allowed.data, { permission: "evaluate", allowed: true });
  await controller.dispose();
});
