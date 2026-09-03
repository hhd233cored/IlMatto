import assert from "node:assert/strict";
import { test } from "node:test";
import os from "node:os";
import path from "node:path";
import { mkdtemp, readFile, rm } from "node:fs/promises";
import { CodexObservationController, CodexObservationStore } from "./codex-observation.js";
import { CodexWorkerError } from "./codex-worker.js";
import type { CodingWorkerEvent } from "./coding-worker.js";

test("Codex observation drafts are persisted and never start Codex", async () => {
  const root = await mkdtemp(path.join(os.tmpdir(), "ilmatto-codex-observation-"));
  try {
    const store = new CodexObservationStore(root);
    const interactions: any[] = [];
    const controller = new CodexObservationController({
      sessionId: "session-1",
      workspacePath: root,
      executable: path.join(root, "missing-codex.exe"),
      onInteraction: (request) => interactions.push(request),
    }, store);

    const response = await controller.handle({ sessionId: "session-1", requestId: "mcp-1", operation: "draft_codex_task", workspacePath: root, prompt: "修复测试" });
    assert.equal(response.ok, true);
    const draft = response.data as any;
    assert.match(draft.draftId, /^draft-/);
    assert.equal(draft.state, "awaiting_user_confirmation");
    assert.equal(interactions.length, 1);
    assert.equal(interactions[0].kind, "question");
    assert.match(interactions[0].details, /修复测试/);
    assert.equal(await readFile(path.join(root, `${draft.draftId}.json`), "utf8").then(Boolean), true);

    await controller.resolveInteraction(draft.draftId, false);
    assert.equal(await store.readDraft(draft.draftId), undefined);
    await controller.dispose();
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("Codex observation rejects a draft outside the Manager workspace", async () => {
  const root = await mkdtemp(path.join(os.tmpdir(), "ilmatto-codex-observation-"));
  const other = await mkdtemp(path.join(os.tmpdir(), "ilmatto-codex-other-"));
  try {
    const controller = new CodexObservationController({ sessionId: "session-1", workspacePath: root, onInteraction: () => undefined }, new CodexObservationStore(root));
    const response = await controller.handle({ sessionId: "session-1", requestId: "mcp-2", operation: "draft_codex_task", workspacePath: other, prompt: "不应提交" });
    assert.equal(response.ok, false);
    assert.equal(response.error?.code, "CODEX_OBSERVATION_ERROR");
    await controller.dispose();
  } finally {
    await rm(root, { recursive: true, force: true });
    await rm(other, { recursive: true, force: true });
  }
});

test("Codex reports are session-scoped and diff reads are bounded", async () => {
  const root = await mkdtemp(path.join(os.tmpdir(), "ilmatto-codex-observation-"));
  try {
    const store = new CodexObservationStore(root);
    const report = {
      taskId: "task-1", sessionId: "session-1", state: "completed" as const, summary: "已完成", changedFiles: [], commands: [], tests: [], warnings: [], pendingQuestions: [],
      startedAt: new Date().toISOString(), completedAt: new Date().toISOString(),
    };
    await store.saveReport(report, "01234567890123456789");
    const controller = new CodexObservationController({ sessionId: "session-1", workspacePath: root, onInteraction: () => undefined }, store);
    assert.equal((await controller.handle({ sessionId: "session-1", requestId: "mcp-3", operation: "get_latest_codex_report" })).ok, true);
    assert.equal((await controller.handle({ sessionId: "other", requestId: "mcp-4", operation: "get_latest_codex_report" })).ok, false);
    const diff = await controller.handle({ sessionId: "session-1", requestId: "mcp-5", operation: "get_codex_diff", taskId: "task-1", maxBytes: 15 });
    assert.equal((diff.data as any).diff.length <= 15, true);
    assert.match((diff.data as any).diff, /diff/);
    await controller.dispose();
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("confirmed Codex drafts create a plain-text task report", async () => {
  const root = await mkdtemp(path.join(os.tmpdir(), "ilmatto-codex-observation-"));
  try {
    const store = new CodexObservationStore(root);
    const interactions: any[] = [];
    const bridge = {
      sessionRef: "thread-test",
      start: async () => undefined,
      sendCodeTask: (taskId: string) => {
        queueMicrotask(() => {
          const emit = (event: CodingWorkerEvent) => bridgeFactoryEvent?.(event);
          emit({ type: "tool_completed", taskId, tool: "run_command", command: "npm test", summary: "命令完成，退出码 0", ok: true });
          emit({ type: "observation_result", taskId, status: "completed", text: "已完成测试和修复。" });
        });
      },
      resolve: () => undefined,
      cancel: () => undefined,
      dispose: async () => undefined,
    } as any;
    let bridgeFactoryEvent: ((event: CodingWorkerEvent) => void) | undefined;
    const controller = new CodexObservationController({
      sessionId: "session-1", workspacePath: root, onInteraction: (request) => interactions.push(request),
      bridgeFactory: (_settings, onEvent) => { bridgeFactoryEvent = onEvent; return bridge; },
    }, store);
    const draftResponse = await controller.handle({ sessionId: "session-1", requestId: "draft", operation: "draft_codex_task", workspacePath: root, prompt: "修复测试" });
    const draft = draftResponse.data as any;
    await controller.resolveInteraction(draft.draftId, true, { answer: "请执行 npm test 并修复失败项" });
    const deadline = Date.now() + 2_000;
    let report: any;
    while (Date.now() < deadline && !report) {
      report = (await controller.handle({ sessionId: "session-1", requestId: "report", operation: "get_latest_codex_report" })).data;
      if (report?.state !== "completed") { report = undefined; await new Promise((resolve) => setTimeout(resolve, 10)); }
    }
    assert.equal(report.state, "completed");
    assert.equal(report.summary, "已完成测试和修复。");
    assert.equal(report.tests[0].status, "passed");
    assert.equal(interactions.length, 1);
    await controller.dispose();
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("Codex progress is projected for the desktop bubble without exposing raw bridge events", async () => {
  const root = await mkdtemp(path.join(os.tmpdir(), "ilmatto-codex-observation-"));
  try {
    const progress: any[] = [];
    let emitEvent: ((event: CodingWorkerEvent) => void) | undefined;
    const bridge = {
      sessionRef: "thread-progress",
      start: async () => undefined,
      sendCodeTask: (taskId: string) => queueMicrotask(() => {
        // Reasoning deltas can split immediately before a space. The
        // observation projection must preserve those boundaries verbatim.
        emitEvent?.({ type: "thinking_delta", taskId, text: "I'm" });
        emitEvent?.({ type: "thinking_delta", taskId, text: " preparing" });
        emitEvent?.({ type: "thinking_delta", taskId, text: " carefully" });
        emitEvent?.({ type: "assistant_delta", taskId, text: "已开始。" });
        emitEvent?.({ type: "tool_started", taskId, callId: "call-1", tool: "run_command", command: "npm test" });
        emitEvent?.({ type: "tool_output", taskId, callId: "call-1", tool: "run_command", text: "通过\nsecret=sk-123456789" });
        emitEvent?.({ type: "tool_completed", taskId, callId: "call-1", tool: "run_command", command: "npm test", summary: "退出码 0", output: "ok", ok: true });
        emitEvent?.({ type: "observation_result", taskId, status: "completed", text: "已完成。" });
      }),
      resolve: () => undefined,
      cancel: () => undefined,
      dispose: async () => undefined,
    } as any;
    const controller = new CodexObservationController({
      sessionId: "session-progress", workspacePath: root, onInteraction: () => undefined,
      onEvent: (event) => progress.push(event),
      bridgeFactory: (_settings, onEvent) => { emitEvent = onEvent; return bridge; },
    }, new CodexObservationStore(root));
    const draft = (await controller.handle({ sessionId: "session-progress", requestId: "draft", operation: "draft_codex_task", workspacePath: root, prompt: "运行测试" })).data as any;
    await controller.resolveInteraction(draft.draftId, true);
    const deadline = Date.now() + 2_000;
    while (Date.now() < deadline && !progress.some((item) => item.type === "completed")) await new Promise((resolve) => setTimeout(resolve, 10));
    assert.deepEqual(progress.map((item) => item.type), ["thinking_delta", "thinking_delta", "thinking_delta", "assistant_delta", "tool_started", "tool_output", "tool_completed", "completed"]);
    assert.equal(progress.filter((item) => item.type === "thinking_delta").map((item) => item.text).join(""), "I'm preparing carefully");
    assert.equal(progress.find((item) => item.type === "tool_output").text.includes("sk-123456789"), false);
    assert.equal(progress.find((item) => item.type === "completed").text, "已完成。");
    await controller.dispose();
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("Codex completion does not replay streamed text, including multiple message items", async () => {
  const root = await mkdtemp(path.join(os.tmpdir(), "ilmatto-codex-observation-"));
  try {
    const progress: any[] = [];
    let emitEvent: ((event: CodingWorkerEvent) => void) | undefined;
    const bridge = {
      sessionRef: "thread-dedup",
      start: async () => undefined,
      sendCodeTask: (taskId: string) => queueMicrotask(() => {
        emitEvent?.({ type: "assistant_delta", taskId, text: "第一段" });
        emitEvent?.({ type: "assistant_delta", taskId, text: "第二段" });
        // The App Server joins separate agent-message items with a newline
        // when it reports the completed turn.
        emitEvent?.({ type: "observation_result", taskId, status: "completed", text: "第一段\n第二段" });
      }),
      resolve: () => undefined,
      cancel: () => undefined,
      dispose: async () => undefined,
    } as any;
    const controller = new CodexObservationController({
      sessionId: "session-dedup", workspacePath: root, onInteraction: () => undefined,
      onEvent: (event) => progress.push(event),
      bridgeFactory: (_settings, onEvent) => { emitEvent = onEvent; return bridge; },
    }, new CodexObservationStore(root));
    const draft = (await controller.handle({ sessionId: "session-dedup", requestId: "draft", operation: "draft_codex_task", workspacePath: root, prompt: "测试去重" })).data as any;
    await controller.resolveInteraction(draft.draftId, true);
    const deadline = Date.now() + 2_000;
    while (Date.now() < deadline && !progress.some((item) => item.type === "completed")) await new Promise((resolve) => setTimeout(resolve, 10));
    assert.deepEqual(progress.filter((item) => item.type === "assistant_delta").map((item) => item.text), ["第一段", "第二段"]);
    assert.equal(progress.find((item) => item.type === "completed")?.text, "");
    await controller.dispose();
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("missing Codex marks the optional channel unavailable without throwing from the session", async () => {
  const root = await mkdtemp(path.join(os.tmpdir(), "ilmatto-codex-observation-"));
  try {
    const store = new CodexObservationStore(root);
    const statuses: any[] = [];
    const controller = new CodexObservationController({
      sessionId: "session-1", workspacePath: root, onInteraction: () => undefined, onStatus: (status) => statuses.push(status),
      bridgeFactory: () => ({
        sessionRef: undefined,
        start: async () => { throw new CodexWorkerError("CODEX_NOT_FOUND", "找不到 Codex CLI"); },
        sendCodeTask: () => undefined,
        resolve: () => undefined,
        cancel: () => undefined,
        dispose: async () => undefined,
      } as any),
    }, store);
    const draft = (await controller.handle({ sessionId: "session-1", requestId: "draft", operation: "draft_codex_task", workspacePath: root, prompt: "尝试 Codex" })).data as any;
    await controller.resolveInteraction(draft.draftId, true);
    const deadline = Date.now() + 2_000;
    let status: any;
    while (Date.now() < deadline && !status) {
      status = (await controller.handle({ sessionId: "session-1", requestId: "status", operation: "get_codex_status" })).data;
      if (status?.state !== "unavailable") { status = undefined; await new Promise((resolve) => setTimeout(resolve, 10)); }
    }
    assert.equal(status.state, "unavailable");
    const report = (await controller.handle({ sessionId: "session-1", requestId: "report", operation: "get_latest_codex_report" })).data as any;
    assert.equal(report.state, "failed");
    assert.ok(statuses.some((item) => item.state === "unavailable"));
    await controller.dispose();
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("explicit Codex submission starts a task without a draft approval interaction", async () => {
  const root = await mkdtemp(path.join(os.tmpdir(), "ilmatto-codex-observation-"));
  try {
    const progress: any[] = [];
    let emitEvent: ((event: CodingWorkerEvent) => void) | undefined;
    const bridge = {
      sessionRef: "thread-explicit",
      start: async () => undefined,
      sendCodeTask: (taskId: string, prompt: string) => queueMicrotask(() => {
        assert.equal(prompt, "直接执行 npm test");
        emitEvent?.({ type: "observation_result", taskId, status: "completed", text: "已完成。" });
      }),
      resolve: () => undefined,
      cancel: () => undefined,
      dispose: async () => undefined,
    } as any;
    const controller = new CodexObservationController({
      sessionId: "session-explicit",
      workspacePath: root,
      onInteraction: () => assert.fail("explicit submission must not create a draft approval interaction"),
      onEvent: (event) => progress.push(event),
      bridgeFactory: (_settings, onEvent) => { emitEvent = onEvent; return bridge; },
    }, new CodexObservationStore(root));
    const taskId = await controller.submitPrompt("直接执行 npm test");
    assert.match(taskId, /^task-/);
    const deadline = Date.now() + 2_000;
    while (Date.now() < deadline && !progress.some((item) => item.type === "completed")) await new Promise((resolve) => setTimeout(resolve, 10));
    assert.equal(progress.find((item) => item.type === "completed")?.text, "已完成。");
    await controller.dispose();
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});
