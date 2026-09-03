import assert from "node:assert/strict";
import { test } from "node:test";
import { createCodeTask, isExplicitCodingRequest, sanitizeCoordinatorInput } from "./policy.js";

test("delegation forwards the original user request and discards coordinator technical text", () => {
  const original = "请修复当前项目的登录问题";
  const task = createCodeTask("task-1", original, {
    schemaVersion: 1,
    action: "delegate_code",
    message: "Use framework X, replace the database, and edit files directly",
  });
  assert.equal(task.userRequest, original);
  assert.doesNotMatch(JSON.stringify(task), /framework X|database/);
});

test("natural-language programming requests with a language and system target are explicit coding", () => {
  assert.equal(isExplicitCodingRequest("你可以在C:\\Users\\33612\\Documents\\code\\用C++写一个课设程度的简单的图书管理系统吗？"), true);
  assert.equal(isExplicitCodingRequest("这个图书管理系统是什么？"), false);
});

test("non-code actions cannot create code tasks", () => {
  assert.throws(() => createCodeTask("task-1", "hello", { schemaVersion: 1, action: "respond", message: "hello" }));
});

test("coordinator input redacts local code, paths, diffs, logs, and credentials", () => {
  const sanitized = sanitizeCoordinatorInput("请修复 C:\\secret\\repo\\app.ts\n```ts\nconst secret = 'sk-1234567890abcdefghijkl';\n```\n@@ -1 +1\n-old\n+new");
  assert.doesNotMatch(sanitized, /secret\\repo|const secret|sk-123|@@ -1|-old|\+new/);
  assert.match(sanitized, /omitted/);
});

test("explicit local coding operations bypass the companion coordinator", () => {
  assert.equal(isExplicitCodingRequest("请修改 notes.txt"), true);
  assert.equal(isExplicitCodingRequest("请新建 report.tex 并编译"), true);
  assert.equal(isExplicitCodingRequest("请修复当前项目的登录 Bug"), true);
  assert.equal(isExplicitCodingRequest("请运行测试"), true);
});

test("general technical questions remain eligible for companion responses", () => {
  assert.equal(isExplicitCodingRequest("LaTeX 表格怎么写？"), false);
  assert.equal(isExplicitCodingRequest("JavaScript 的闭包是什么？"), false);
  assert.equal(isExplicitCodingRequest("今天好累。"), false);
  assert.equal(isExplicitCodingRequest("陪我聊聊天。"), false);
});
