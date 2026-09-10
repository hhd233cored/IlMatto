import assert from "node:assert/strict";
import { mkdir, mkdtemp, readFile, rm, stat } from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import { test } from "node:test";
import { CompanionMemoryStore, MAX_SNIPPET_CHARS, PROFILE_MAX_CHARS, SUMMARY_MAX_CHARS } from "./companion-memory.js";

async function withStore(run: (store: CompanionMemoryStore, root: string) => Promise<void>): Promise<void> {
  const root = await mkdtemp(path.join(os.tmpdir(), "ilmatto-companion-memory-"));
  try { await run(new CompanionMemoryStore(root), root); }
  finally { await rm(root, { recursive: true, force: true }); }
}

test("ensureTranscript seeds once and leaves existing history unchanged", async () => {
  await withStore(async (store, root) => {
    const history = [{ role: "user" as const, text: "完整旧记录", createdAt: "2026-09-10T00:00:00Z" }];
    await store.ensureTranscript("seeded", history);
    const transcriptPath = path.join(root, "sessions", "seeded", "transcript.jsonl");
    const before = await readFile(transcriptPath, "utf8");
    await store.ensureTranscript("seeded", [{ ...history[0], text: "不得追加或替换" }]);
    assert.equal(await readFile(transcriptPath, "utf8"), before);
  });
});

test("profile updates merge into a readable markdown file and are bounded", async () => {
  await withStore(async (store) => {
    assert.equal(await store.readProfile(), "# 用户画像\n\n## 基本信息\n\n## 兴趣\n\n## 互动偏好\n\n## 边界与注意事项\n\n## 当前关注");
    await store.updateProfile({ section: "preferences", add: ["喜欢慢节奏剧情", "喜欢慢节奏剧情"] });
    const profile = await store.readProfile();
    assert.equal(profile.match(/喜欢慢节奏剧情/g)?.length, 1);
    assert.match(profile, /## 互动偏好[\s\S]*- 喜欢慢节奏剧情/);
    await store.updateProfile({ section: "preferences", remove: ["喜欢慢节奏剧情"] });
    assert.doesNotMatch(await store.readProfile(), /喜欢慢节奏剧情/);
    await store.updateProfile({ section: "basic", add: ["x".repeat(PROFILE_MAX_CHARS)] });
    assert.ok((await store.readProfile()).length <= PROFILE_MAX_CHARS);
    assert.ok((await readFile(store.profilePath, "utf8")).length <= PROFILE_MAX_CHARS + 1);
  });
});

test("concurrent profile updates preserve both durable facts", async () => {
  await withStore(async (store) => {
    await Promise.all([
      store.updateProfile({ section: "basic", add: ["用户使用中文"] }),
      store.updateProfile({ section: "preferences", add: ["喜欢简洁回复"] }),
    ]);
    const profile = await store.readProfile();
    assert.match(profile, /用户使用中文/);
    assert.match(profile, /喜欢简洁回复/);
  });
});

test("summary patches are merged, deduplicated, and searchable without transcript leakage", async () => {
  await withStore(async (store) => {
    await store.updateSummary("session-1", {
      title: "旧信件调查",
      summaryPatch: "用户与角色在车站讨论了旧信件。",
      keyEvents: ["讨论旧信件", "讨论旧信件"],
      openLoops: ["继续调查旧信件来源"],
      keywords: ["旧信件", "车站"],
    });
    await store.updateSummary("session-1", { summaryPatch: "双方约定下次继续调查。", keyEvents: ["讨论旧信件"] });
    await store.appendTranscript("session-1", [
      { role: "user", text: "我们下次继续调查那封旧信件。", createdAt: "2026-09-05T00:00:00Z" },
      { role: "assistant", text: "好，我会记得车站和旧信件。", createdAt: "2026-09-05T00:00:01Z" },
    ]);
    const results = await store.searchSessions("旧信件");
    assert.equal(results.length, 1);
    assert.equal(results[0].sessionId, "session-1");
    assert.equal(results[0].keyEvents.filter((item) => item === "讨论旧信件").length, 1);
    assert.equal(JSON.stringify(results).includes("下次继续调查那封旧信件"), false);
    const opened = await store.openSession("session-1", "旧信件");
    assert.equal(opened.snippets.length, 1);
    assert.match(JSON.stringify(opened), /旧信件/);
    assert.ok(JSON.stringify(opened).length <= MAX_SNIPPET_CHARS + 200);
    await store.updateSummary("session-1", { summaryPatch: "x".repeat(SUMMARY_MAX_CHARS * 2) });
    const summary = await store.readSummary("session-1");
    assert.ok(summary.summary.length <= SUMMARY_MAX_CHARS);
  });
});

test("session ids are confined to the memory sessions directory", () => {
  assert.throws(() => new CompanionMemoryStore("C:\\memory").getSessionDirectory("..\\outside"), /无效/);
  assert.throws(() => new CompanionMemoryStore("C:\\memory").getSessionDirectory("../outside"), /无效/);
});

test("deleting a session removes only its memory and preserves the shared profile", async () => {
  await withStore(async (store) => {
    await store.readProfile("# 用户画像\n\n## 基本信息\n\n- 用户使用中文");
    await store.updateSummary("session-delete", { summaryPatch: "只属于待删除会话的内容。" });
    await store.appendTranscript("session-delete", [{ role: "user", text: "临时会话内容", createdAt: "2026-09-07T00:00:00Z" }]);
    await store.deleteSession("session-delete");
    await assert.rejects(stat(path.join(path.dirname(store.profilePath), "sessions", "session-delete")));
    assert.match(await store.readProfile(), /用户使用中文/);
  });
});

test("session open returns no result when the transcript is unavailable", async () => {
  await withStore(async (store) => {
    const opened = await store.openSession("missing-session", "任何内容");
    assert.deepEqual(opened, { sessionId: "missing-session", snippets: [], truncated: false });
  });
});

test("an unreadable profile degrades to an empty profile", async () => {
  await withStore(async (store) => {
    await mkdir(store.profilePath);
    assert.equal(await store.readProfile(), "");
  });
});
