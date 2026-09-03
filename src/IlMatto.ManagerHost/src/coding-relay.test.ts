import assert from "node:assert/strict";
import { test } from "node:test";
import { buildCodingProgressPrompt, buildCodingResultPrompt, codingRelayEnabled, redactRelayText } from "./coding-relay.js";

test("coding relay is enabled only for Antigravity plus Codex", () => {
  assert.equal(codingRelayEnabled("antigravity", "codex"), true);
  assert.equal(codingRelayEnabled("antigravity", "pi"), false);
  assert.equal(codingRelayEnabled("api_manager", "codex"), false);
});

test("coding relay progress never includes command details", () => {
  const prompt = buildCodingProgressPrompt({ type: "tool_started", tool: "run_command", command: "Get-Content C:\\secret\\file.txt" });
  assert.ok(prompt);
  assert.match(prompt, /run_command/);
  assert.doesNotMatch(prompt, /secret|Get-Content|file\.txt/);
});

test("coding relay result keeps bounded facts and omits changed file paths", () => {
  const prompt = buildCodingResultPrompt({
    status: "completed", summaryForUser: "已修改 C:\\repo\\src\\Main.cs 并完成验证。", technicalDecisions: [],
    filesChanged: [{ path: "src/Main.cs", additions: 3, deletions: 1 }],
    validation: [{ command: "dotnet test C:\\repo", status: "passed", summary: "测试通过" }], questions: [], needsUserDecision: false,
  });
  assert.doesNotMatch(prompt, /Main\.cs|C:\\repo|dotnet test/);
  assert.match(prompt, /"filesChanged":1/);
  assert.match(prompt, /"additions":3/);
  assert.match(prompt, /"deletions":1/);
});

test("relay redaction bounds and removes absolute paths", () => {
  const value = redactRelayText("see C:\\repo\\file.cs and /home/user/file.txt", 40);
  assert.doesNotMatch(value, /C:\\repo|\/home\/user/);
  assert.ok(value.length <= 40);
});
