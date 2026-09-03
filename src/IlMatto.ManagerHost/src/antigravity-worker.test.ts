import assert from "node:assert/strict";
import { test } from "node:test";
import { commandExecutionPolicy, extractAntigravityWorkerStep, parseAntigravityWorkerResult, renderExecutorAgent } from "./antigravity-worker.js";

test("Antigravity coding worker reads documented step_update envelopes", () => {
  const step = extractAntigravityWorkerStep({
    event: "step_update",
    step_update: {
      step_type: "tool",
      state: "DONE",
      tool_name: "run_command",
      tool_info: { parameters: { CommandLine: "npm test" }, output: "passed\r\n" },
    },
  });
  assert.equal(step?.tool_name, "run_command");
  assert.equal(step?.tool_info?.parameters?.CommandLine, "npm test");
  assert.equal(step?.tool_info?.output, "passed\r\n");
});

test("Antigravity coding worker reads structured_output from the terminal result", () => {
  const result = parseAntigravityWorkerResult({
    event: "result",
    result: {
      status: "SUCCESS",
      structured_output: {
        status: "completed", summaryForUser: "完成", technicalDecisions: [], filesChanged: [], validation: [], questions: [], needsUserDecision: false,
      },
    },
  });
  assert.equal(result?.status, "completed");
});

test("Antigravity coding agent uses discoverable tools and an explicit role boundary", () => {
  const settings = { sessionId: "s", workspacePath: "C:\\workspace", executionPolicy: "autonomous" as const, mode: "implementation" as const };
  const definition = renderExecutorAgent("ilmatto-executor-test", settings);
  assert.match(definition, /^  - view_file\s*$/m);
  assert.match(definition, /^  - grep_search\s*$/m);
  assert.match(definition, /^  - write_to_file\s*$/m);
  assert.match(definition, /^  - replace_file_content\s*$/m);
  assert.match(definition, /^  - run_command\s*$/m);
  assert.match(definition, /mainAgent: false/);
  assert.match(definition, /subagent: true/);
  assert.match(definition, /commandExecutionPolicy: eager/);
  assert.doesNotMatch(definition, /read_file|write_file|create_file|edit_file|always-proceed|request-review/);
});

test("verification worker remains sandboxed and read-only", () => {
  const settings = { sessionId: "s", workspacePath: "C:\\workspace", executionPolicy: "autonomous" as const, mode: "verification" as const };
  const definition = renderExecutorAgent("ilmatto-verifier-test", settings);
  assert.equal(commandExecutionPolicy(settings), "auto");
  assert.match(definition, /^  - view_file\s*$/m);
  assert.match(definition, /^  - grep_search\s*$/m);
  assert.match(definition, /^  - run_command\s*$/m);
  assert.doesNotMatch(definition, /write_to_file|replace_file_content/);
  assert.match(definition, /Verification only\. Never edit source files\./);
});
