import assert from "node:assert/strict";
import { EventEmitter } from "node:events";
import { test } from "node:test";
import { mkdtemp, readFile, rm } from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import { AntigravitySession, AntigravitySessionError, coordinatorStepPolicyViolation, execFileWithClosedStdin, extractAntigravityProgress, extractManagerMessage, isAuthenticationError, isManagerAgentFallback, parseAntigravityModels, parseAntigravityResult, shouldPassAntigravityEffort, validateAntigravityInit } from "./antigravity.js";
import type { ManagerRuntime } from "./runtime.js";

test("Antigravity authentication failures are recognized without exposing credentials", () => {
  assert.equal(isAuthenticationError("authentication required"), true);
  assert.equal(isAuthenticationError("Not logged in. Run agy first."), true);
  assert.equal(isAuthenticationError("model not found"), false);
});

test("Antigravity probe commands receive EOF on stdin", async () => {
  const script = "if (process.argv.includes('wait-for-eof')) { process.stdin.on('end', () => process.stdout.write('done')); process.stdin.resume(); } else process.stdout.write('version');";
  const result = await execFileWithClosedStdin(process.execPath, ["-e", script, "wait-for-eof"], { timeout: 2_000, windowsHide: true });
  assert.equal(result.stdout, "done");
});

test("Antigravity model parsing accepts table and slug output without leaking metadata", () => {
  const models = parseAntigravityModels("Available models:\n gemini-2.5-pro  Gemini Pro\n* claude-sonnet-4\nnot a model");
  assert.deepEqual(models.map((item) => [item.id, item.displayName]), [
    ["gemini-2.5-pro", "Gemini Pro"],
    ["claude-sonnet-4", "claude-sonnet-4"],
  ]);
});

test("Antigravity result parsing preserves conversation and cache metrics", () => {
  const parsed = parseAntigravityResult({ conversation_id: "conversation-1", structured_output: { schemaVersion: 1, action: "respond", message: "hello" }, usage: { cache_read_tokens: 8123 } });
  assert.equal(parsed.conversationId, "conversation-1");
  assert.equal(parsed.cacheReadTokens, 8123);
  assert.equal(parsed.action.message, "hello");
});

test("Antigravity result parsing accepts structured JSON strings and fenced responses", () => {
  const response = "```json\n{\"schemaVersion\":1,\"action\":\"respond\",\"message\":\"hello\"}\n```";
  assert.equal(parseAntigravityResult({ response }).action.message, "hello");
  assert.equal(parseAntigravityResult({ structured_output: JSON.stringify({ schemaVersion: 1, action: "ask_user", message: "which one?" }) }).action.action, "ask_user");
  assert.equal(parseAntigravityResult({ structured_output: { unexpected: true }, response: JSON.stringify({ schemaVersion: 1, action: "respond", message: "fallback" }) }).action.message, "fallback");
});

test("Antigravity stream exposes a short progress summary when no reasoning delta is sent", () => {
  const streamed = '{"action":"respond","message":"你好","toolAction":"Responding to user greeting","toolSummary":"Greeting response"}';
  assert.equal(extractAntigravityProgress(streamed), "Responding to user greeting · Greeting response");
  assert.equal(extractAntigravityProgress('{"action":"respond","message":"你好"}'), "");
});

test("accepts AGY responses that omit the const schemaVersion field", () => {
  const response = "```json\n{\"action\":\"delegate_code\",\"message\":\"\"}\n```";
  assert.deepEqual(parseAntigravityResult({ response }).action, { schemaVersion: 1, action: "delegate_code", message: "" });
});

test("manager response stream extracts escaped message text incrementally", () => {
  const first = extractManagerMessage('{"schemaVersion":1,"action":"respond","message":"hello\\nwo');
  assert.equal(first.found, true);
  assert.equal(first.text, "hello\nwo");
  const complete = extractManagerMessage('{"schemaVersion":1,"action":"respond","message":"hello\\nworld"}');
  assert.equal(complete.text, "hello\nworld");
  assert.equal(extractManagerMessage('{"schemaVersion":1,"action":"respond"}').found, false);
});

test("init validates the selected agent and isolated cwd without treating the global tool catalogue as permissions", () => {
  const root = "C:\\runtime\\ilmatto";
  assert.equal(validateAntigravityInit({ agent: "ilmatto-manager", cwd: root, permission_mode: "request-review", tools: ["runcommand", "viewfile", "invokesubagent"] }, root), undefined);
  assert.match(validateAntigravityInit({ agent: "default", cwd: root }, root) ?? "", /required ilmatto-manager/);
  assert.match(validateAntigravityInit({ agent: "ilmatto-manager", cwd: "C:\\workspace" }, root) ?? "", /outside its isolated runtime/);
  assert.match(validateAntigravityInit({ agent: "ilmatto-manager", cwd: root, permission_mode: "always-proceed" }, root) ?? "", /always-proceed/);
  assert.equal(validateAntigravityInit({ agent: "ilmatto-manager-a1b2c3", cwd: root, permission_mode: "request-review" }, root, "ilmatto-manager-a1b2c3"), undefined);
});

test("coordinator rejects coding tools and non-browser subagents", () => {
  assert.equal(coordinatorStepPolicyViolation({ step_type: "agent_response" }), undefined);
  assert.match(coordinatorStepPolicyViolation({ step_type: "tool", tool_name: "run_command" }) ?? "", /forbidden tool: run_command/);
  assert.match(coordinatorStepPolicyViolation({ step_type: "agent_response", subagent_info: { role: "research" } }) ?? "", /non-browser subagent/);
});

test("coordinator allows web research and the built-in browser only", () => {
  assert.equal(coordinatorStepPolicyViolation({ step_type: "tool", tool_name: "search_web", tool_info: { parameters: { query: "weather" } } }), undefined);
  assert.equal(coordinatorStepPolicyViolation({ step_type: "tool", tool_name: "read_url_content", tool_info: { parameters: { Url: "https://example.com" } } }), undefined);
  assert.equal(coordinatorStepPolicyViolation({ step_type: "tool", tool_name: "browser_open", tool_info: { parameters: { url: "https://example.com" } } }), undefined);
  assert.equal(coordinatorStepPolicyViolation({ step_type: "tool", tool_name: "invoke_subagent", tool_info: { parameters: { subagent_type: "browser" } } }), undefined);
  assert.equal(coordinatorStepPolicyViolation({ step_type: "agent_response", subagent_info: { type_name: "browser" } }), undefined);
  assert.match(coordinatorStepPolicyViolation({ step_type: "tool", tool_name: "invoke_subagent", tool_info: { parameters: { subagent_type: "research" } } }) ?? "", /browser subagent/);
});

test("coordinator allows view_file only for the current managed image path", () => {
  const allowed = "C:\\runtime\\attachments\\session\\image.png";
  const step = { step_type: "tool", tool_name: "view_file", tool_info: { parameters: { AbsolutePath: allowed } } };
  assert.equal(coordinatorStepPolicyViolation(step, [allowed]), undefined);
  assert.equal(coordinatorStepPolicyViolation({ ...step, tool_info: { parameters: { AbsolutePath: `"${allowed}"` } } }, [allowed]), undefined);
  assert.equal(coordinatorStepPolicyViolation({ ...step, tool_info: { parameters: { AbsolutePath: "file:///C:/runtime/attachments/session/image.png" } } }, [allowed]), undefined);
  assert.equal(coordinatorStepPolicyViolation({ step_type: "tool", tool_name: "readfile", tool_info: { parameters: { AbsolutePath: allowed } } }, [allowed]), undefined);
  assert.equal(coordinatorStepPolicyViolation({ step_type: "tool", tool_name: "read_file", tool_info: { parameters: { AbsolutePath: allowed } } }, [allowed]), undefined);
  assert.match(coordinatorStepPolicyViolation(step) ?? "", /current managed image set/);
  assert.match(coordinatorStepPolicyViolation({ ...step, tool_info: { parameters: { AbsolutePath: "C:\\runtime\\other.txt" } } }, [allowed]) ?? "", /current managed image set/);
  assert.match(coordinatorStepPolicyViolation({ step_type: "tool", tool_name: "view_file", tool_info: { parameters: {} } }, [allowed]) ?? "", /verifiable image path/);
});

test("Antigravity exposes each new tool call as a distinct stream event", async () => {
  const process = new ScriptedAntigravityProcess((instance, _prompt, turn) => {
    if (turn === 1) {
      instance.emitInit();
      instance.emitTool("search_web", "search-1");
      instance.emitTool("search_web", "search-1");
      instance.emitStep("搜索完成。 ");
      instance.emitTool("read_url_content", "read-1");
      instance.emitStep("网页内容已读取。 ");
      instance.emitResult(successResult("done"));
    }
  });
  const session = new AntigravitySession("agy", testRuntime(), 2, "medium", undefined, undefined, ((_file: string, _args: string[]) => process) as any);
  try {
    const events: Array<{ kind: string; text: string; callId?: string }> = [];
    await session.ask("查一下这个页面", (event) => events.push({ kind: event.kind, text: event.text, callId: event.kind === "tool" ? event.callId : undefined }));
    assert.deepEqual(events.map((event) => event.kind), ["tool", "thinking", "text", "tool", "text"]);
    assert.deepEqual(events.filter((event) => event.kind === "tool").map((event) => event.callId), ["search-1", "read-1"]);
    assert.equal(events[0].text, "正在搜索网页…");
    assert.equal(events[3].text, "正在读取网页内容…");
  } finally {
    session.dispose();
  }
});

test("manager detects AGY silent fallback to the default agent", () => {
  assert.equal(isManagerAgentFallback('Agent "ilmatto-manager-a1b2c3" not found, falling back to default', "ilmatto-manager-a1b2c3"), true);
  assert.equal(isManagerAgentFallback('Agent "other" not found, falling back to default', "ilmatto-manager-a1b2c3"), false);
});

test("pinned Antigravity model variants do not receive a duplicate effort flag", () => {
  assert.equal(shouldPassAntigravityEffort("gemini-3.7-flash-high"), false);
  assert.equal(shouldPassAntigravityEffort("gemini-3.7-flash-medium"), false);
  assert.equal(shouldPassAntigravityEffort("gemini-3.7-flash"), true);
  assert.equal(shouldPassAntigravityEffort(undefined), true);
});

test("a complete streamed action followed by a clean exit is still a successful turn", async () => {
  const launches: string[][] = [];
  const runtime = { root: "C:\\runtime\\ilmatto", schemaPath: "C:\\runtime\\schema.json", logPath: "C:\\runtime\\agy.log", agentName: "ilmatto-manager-test" } as ManagerRuntime;
  const spawnProcess = ((_file: string, args: string[]) => {
    launches.push(args);
    return new FakeAntigravityProcess();
  }) as any;
  const session = new AntigravitySession("agy", runtime, 2, "medium", undefined, undefined, spawnProcess);
  try {
    const streamed: string[] = [];
    const first = await session.ask("看这张图片", (event) => streamed.push(`${event.kind}:${event.text}`), []);
    assert.equal(first.action.action, "respond");
    assert.equal(first.action.message, "我看到了图片。");
    assert.ok(streamed.some((event) => event === "text:我看到了图片。"));

    const second = await session.ask("继续说", undefined, []);
    assert.equal(second.action.message, "我看到了图片。");
    assert.equal(launches.length, 2);
    assert.deepEqual(launches[1].slice(-2), ["--conversation", "conversation-image"]);
  } finally {
    session.dispose();
  }
});

test("one live stream-json process handles consecutive turns without restarting", async () => {
  const launches: string[][] = [];
  const process = new ScriptedAntigravityProcess((instance, prompt, turn) => {
    if (turn === 1) instance.emitInit();
    instance.emitResult(successResult(`reply-${turn}`));
  });
  const runtime = testRuntime();
  const session = new AntigravitySession("agy", runtime, 2, "medium", undefined, undefined, ((_file: string, args: string[]) => {
    launches.push(args);
    return process;
  }) as any);
  try {
    assert.equal((await session.ask("first")).action.message, "reply-1");
    assert.equal((await session.ask("second")).action.message, "reply-2");
    assert.equal(launches.length, 1);
    assert.equal(process.prompts.length, 2);
  } finally {
    session.dispose();
  }
});

test("a failed AGY conversation resume falls back to persisted history once", async () => {
  const root = await mkdtemp(path.join(os.tmpdir(), "ilmatto-antigravity-resume-"));
  const runtime = { root, schemaPath: path.join(root, "schema.json"), logPath: path.join(root, "agy.log"), agentName: "ilmatto-manager-test" } as ManagerRuntime;
  const failedResume = new ScriptedAntigravityProcess((instance) => {
    instance.emitInit("saved-conversation");
    instance.emitResult({ conversation_id: "saved-conversation", status: "ERROR", error: "conversation not found" });
  });
  const recovered = new ScriptedAntigravityProcess((instance) => {
    instance.emitInit("fresh-conversation");
    instance.emitResult({ conversation_id: "fresh-conversation", status: "SUCCESS", response: "recovered" });
  });
  const processes = [failedResume, recovered];
  const launches: string[][] = [];
  const session = new AntigravitySession(
    "agy", runtime, 2, "medium", undefined, "saved-conversation",
    ((_file: string, args: string[]) => { launches.push(args); return processes.shift()!; }) as any,
    "restart-resume-test", false,
    [{ role: "user", text: "之前我们讨论过会话恢复" }, { role: "assistant", text: "这是历史上下文" }],
  );
  try {
    const turn = await session.ask("继续当前任务");
    assert.equal(turn.text, "recovered");
    assert.equal(launches.length, 2);
    assert.deepEqual(launches[0].slice(-2), ["--conversation", "saved-conversation"]);
    assert.equal(launches[1].includes("--conversation"), false);
    assert.match(recovered.prompts[0], /<saved_conversation_history>/);
    assert.match(recovered.prompts[0], /之前我们讨论过会话恢复/);
    assert.match(recovered.prompts[0], /继续当前任务/);

    // A recovery transcript is one-shot: the next turn uses the live fresh
    // conversation and does not prepend the saved transcript again.
    const next = session.ask("下一条消息");
    await next;
    assert.equal(recovered.prompts.length, 2);
    assert.equal(recovered.prompts[1].includes("<saved_conversation_history>"), false);

    await new Promise((resolve) => setTimeout(resolve, 25));
    const log = await readFile(path.join(root, "antigravity-session.log"), "utf8");
    assert.match(log, /"conversationId":"saved-conversation"/);
    assert.match(log, /"resumedConversation":false/);
    assert.match(log, /"historyMessageCount":2/);
    assert.match(log, /history_fallback_injected/);
  } finally {
    session.dispose();
    await rm(root, { recursive: true, force: true });
  }
});

test("a successful AGY conversation resume is recorded in lifecycle diagnostics", async () => {
  const root = await mkdtemp(path.join(os.tmpdir(), "ilmatto-antigravity-resume-success-"));
  const runtime = { root, schemaPath: path.join(root, "schema.json"), logPath: path.join(root, "agy.log"), agentName: "ilmatto-manager-test" } as ManagerRuntime;
  const process = new ScriptedAntigravityProcess((instance) => {
    instance.emitInit("persisted-conversation");
    instance.emitResult({ conversation_id: "persisted-conversation", status: "SUCCESS", response: "resumed" });
  });
  const session = new AntigravitySession(
    "agy", runtime, 2, "medium", undefined, "persisted-conversation", (() => process) as any,
    "restart-resume-success-test", false, [{ role: "user", text: "历史消息" }],
  );
  try {
    assert.equal((await session.ask("继续")).text, "resumed");
    await new Promise((resolve) => setTimeout(resolve, 25));
    const log = await readFile(path.join(root, "antigravity-session.log"), "utf8");
    assert.match(log, /"conversationId":"persisted-conversation"/);
    assert.match(log, /"resumedConversation":true/);
    assert.match(log, /"historyMessageCount":1/);
  } finally {
    session.dispose();
    await rm(root, { recursive: true, force: true });
  }
});

test("unified text mode uses one full-permission AGY process without schema flags", async () => {
  const launches: string[][] = [];
  const process = new ScriptedAntigravityProcess((instance) => {
    instance.emitInit();
    instance.emitStep("已完成文件检查和测试。");
    instance.emitResult({ status: "SUCCESS", response: "已完成文件检查和测试。" });
  });
  const session = new AntigravitySession("agy", testRuntime(), 2, "medium", undefined, undefined, ((_file: string, args: string[]) => {
    launches.push(args);
    return process;
  }) as any, "unified-test", false);
  try {
    const events: string[] = [];
    const turn = await session.ask("请检查并测试项目", (event) => { if (event.kind === "text") events.push(event.text); });
    assert.equal(turn.text, "已完成文件检查和测试。");
    assert.equal(events.join(""), "已完成文件检查和测试。");
    const args = launches[0];
    assert.ok(args.includes("--dangerously-skip-permissions"));
    assert.ok(args.includes("--mode") && args.includes("accept-edits"));
    assert.equal(args.includes("--agent"), false);
    assert.equal(args.includes("--json-schema"), false);
    assert.equal(args.includes("--sandbox"), false);
  } finally {
    session.dispose();
  }
});

test("unified text mode accepts nested AGY response text", async () => {
  const process = new ScriptedAntigravityProcess((instance) => {
    instance.emitInit();
    instance.emitResult({ status: "SUCCESS", response: { content: [{ text: "嵌套文本" }] } });
  });
  const session = new AntigravitySession("agy", testRuntime(), 2, "medium", undefined, undefined, (() => process) as any, "unified-nested", false);
  try {
    const turn = await session.ask("返回文本");
    assert.equal(turn.text, "嵌套文本");
  } finally {
    session.dispose();
  }
});

test("an exit before stdout closes drains the final result instead of failing the turn", async () => {
  const processes = [new ScriptedAntigravityProcess((instance) => {
    instance.emitInit();
    const event = JSON.stringify({ event: "result", result: successResult("drained") });
    instance.stdout.emit("data", event.slice(0, -1));
    instance.emit("exit", 0, null);
    instance.stdout.emit("data", `${event.slice(-1)}\n`);
    instance.stdout.emit("end");
    instance.emit("close", 0, null);
  }), new ScriptedAntigravityProcess((instance) => instance.exitCleanly())];
  const launches: string[][] = [];
  const session = new AntigravitySession("agy", testRuntime(), 2, "medium", undefined, undefined, ((_file: string, args: string[]) => {
    launches.push(args);
    return processes.shift()!;
  }) as any);
  try {
    const turn = await session.ask("describe image");
    assert.equal(turn.action.message, "drained");
    assert.equal(launches.length, 1);
  } finally {
    session.dispose();
  }
});

test("a valid streamed action recovers an invalid final result without restarting", async () => {
  const launches: string[][] = [];
  const process = new ScriptedAntigravityProcess((instance) => {
    instance.emitInit();
    instance.emitStep(JSON.stringify({ schemaVersion: 1, action: "respond", message: "stream recovery" }));
    instance.emitResult({ status: "SUCCESS", response: "not a manager action" });
  });
  const session = new AntigravitySession("agy", testRuntime(), 2, "medium", undefined, undefined, ((_file: string, args: string[]) => {
    launches.push(args);
    return process;
  }) as any);
  try {
    const turn = await session.ask("image question");
    assert.equal(turn.action.message, "stream recovery");
    assert.equal(launches.length, 1);
  } finally {
    session.dispose();
  }
});

test("a stale process exit cannot reject a new process turn", async () => {
  const first = new ScriptedAntigravityProcess(() => undefined);
  const second = new ScriptedAntigravityProcess((instance) => {
    instance.emitInit();
    instance.emitResult(successResult("new process reply"));
  });
  const processes = [first, second];
  const session = new AntigravitySession("agy", testRuntime(), 2, "medium", undefined, undefined, (() => processes.shift()!) as any);
  const firstTurn = session.ask("first");
  session.cancel();
  await assert.rejects(firstTurn, /cancelled/);
  const secondTurn = session.ask("second");
  first.exitCleanly();
  assert.equal((await secondTurn).action.message, "new process reply");
  session.dispose();
});

test("an incomplete streamed response exits as AGY_EARLY_EXIT without a hidden repair process", async () => {
  const processes = [new ScriptedAntigravityProcess((instance) => {
    instance.emitInit();
    instance.emitStep('{"schemaVersion":1,"action":"respond","message":"partial');
    instance.exitCleanly();
  }), new ScriptedAntigravityProcess((instance) => instance.exitCleanly())];
  const launches: string[][] = [];
  const session = new AntigravitySession("agy", testRuntime(), 2, "medium", undefined, undefined, (() => {
    launches.push([]);
    return processes.shift()!;
  }) as any);
  try {
    await assert.rejects(session.ask("image question"), (error: unknown) => error instanceof AntigravitySessionError && error.code === "AGY_EARLY_EXIT");
    assert.equal(launches.length, 1);
  } finally {
    session.dispose();
  }
});

test("a schema repair stays in the same live process when no text has been streamed", async () => {
  const launches: string[][] = [];
  const process = new ScriptedAntigravityProcess((instance, _prompt, turn) => {
    if (turn === 1) {
      instance.emitInit();
      instance.emitResult({ status: "SUCCESS", response: "not a manager action" });
      return;
    }
    instance.emitResult(successResult("repaired"));
  });
  const session = new AntigravitySession("agy", testRuntime(), 2, "medium", undefined, undefined, ((_file: string, args: string[]) => {
    launches.push(args);
    return process;
  }) as any);
  try {
    const turn = await session.ask("question");
    assert.equal(turn.action.message, "repaired");
    assert.equal(process.prompts.length, 2);
    assert.match(process.prompts[1], /previous response violated/i);
    assert.equal(launches.length, 1);
  } finally {
    session.dispose();
  }
});

function testRuntime(): ManagerRuntime {
  return { root: "C:\\runtime\\ilmatto", schemaPath: "C:\\runtime\\schema.json", logPath: "C:\\runtime\\agy.log", agentName: "ilmatto-manager-test" } as ManagerRuntime;
}

function successResult(message: string): Record<string, unknown> {
  return { conversation_id: "conversation-live", status: "SUCCESS", response: JSON.stringify({ schemaVersion: 1, action: "respond", message }) };
}

class ScriptedAntigravityProcess extends EventEmitter {
  readonly killed = false;
  readonly prompts: string[] = [];
  readonly stdin = {
    writable: true,
    write: (chunk: string, _encoding: string, callback?: (error?: Error) => void) => {
      this.prompts.push(JSON.parse(chunk).message.content);
      callback?.();
      const turn = this.prompts.length;
      queueMicrotask(() => this.script(this, this.prompts[turn - 1], turn));
      return true;
    },
    end: () => undefined,
  };
  readonly stdout = Object.assign(new EventEmitter(), { setEncoding: (_encoding: string) => undefined });
  readonly stderr = Object.assign(new EventEmitter(), { setEncoding: (_encoding: string) => undefined });

  constructor(private readonly script: (instance: ScriptedAntigravityProcess, prompt: string, turn: number) => void) { super(); }

  emitInit(conversationId = "conversation-live"): void {
    this.stdout.emit("data", `${JSON.stringify({ event: "init", conversation_id: conversationId, init: { agent: "ilmatto-manager-test", cwd: "C:\\runtime\\ilmatto", permission_mode: "request-review" } })}\n`);
  }

  emitStep(textDelta: string): void {
    this.stdout.emit("data", `${JSON.stringify({ event: "step_update", step_update: { step_type: "agent_response", text_delta: textDelta } })}\n`);
  }

  emitTool(toolName: string, callId: string): void {
    this.stdout.emit("data", `${JSON.stringify({ event: "step_update", step_update: { step_type: "tool", tool_name: toolName, call_id: callId } })}\n`);
  }

  emitResult(result: Record<string, unknown>): void {
    this.stdout.emit("data", `${JSON.stringify({ event: "result", result })}\n`);
  }

  exitCleanly(): void {
    this.emit("exit", 0, null);
    this.stdout.emit("end");
    this.emit("close", 0, null);
  }

  kill(): void { }
}

class FakeAntigravityProcess extends EventEmitter {
  readonly pid = 12345;
  killed = false;
  readonly stdin = {
    writable: true,
    write: (_chunk: string, _encoding: string, callback?: (error?: Error) => void) => {
      callback?.();
      queueMicrotask(() => {
        this.stdout.emit("data", `${JSON.stringify({ event: "init", conversation_id: "conversation-image", init: { agent: "ilmatto-manager-test", cwd: "C:\\runtime\\ilmatto", permission_mode: "request-review" } })}\n`);
        this.stdout.emit("data", `${JSON.stringify({ event: "step_update", step_update: { step_type: "agent_response", text_delta: JSON.stringify({ schemaVersion: 1, action: "respond", message: "我看到了图片。" }) } })}\n`);
        this.emit("exit", 0, null);
        this.stdout.emit("end");
        this.emit("close", 0, null);
      });
      return true;
    },
    end: () => undefined,
  };
  readonly stdout = Object.assign(new EventEmitter(), { setEncoding: (_encoding: string) => undefined });
  readonly stderr = Object.assign(new EventEmitter(), { setEncoding: (_encoding: string) => undefined });
}
