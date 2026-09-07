import assert from "node:assert/strict";
import { test } from "node:test";
import { buildCompanionSystemPrompt, defaultCompanionProfile, normalizeCompanionProfile } from "./companion.js";
test("companion prompt contains profile data and fixed routing rules", () => {
    const prompt = buildCompanionSystemPrompt({
        characterName: "Luna",
        characterPrompt: "角色名是 Luna，语气克制。",
        userProfile: "用户喜欢雨天。",
        relationshipSummary: "旧版关系摘要不应继续进入活动 prompt。",
    });
    assert.match(prompt, /角色名是 Luna/);
    assert.match(prompt, /role name is "Luna"/i);
    assert.doesNotMatch(prompt, /用户喜欢雨天/);
    assert.doesNotMatch(prompt, /旧版关系摘要/);
    assert.match(prompt, /search_web/);
    assert.match(prompt, /read_url_content/);
    assert.match(prompt, /built-in browser subagent/i);
    assert.match(prompt, /no general workspace, file, terminal, command execution, Git, skill, plugin, or MCP access/i);
    assert.match(prompt, /manage_task, schedule, list_permissions, ask_permission/i);
    assert.match(prompt, /manage_subagents, define_subagent, send_message, run_command/i);
    assert.match(prompt, /delegate_code/);
    assert.match(prompt, /respond/);
});
test("missing companion fields use safe defaults", () => {
    const profile = normalizeCompanionProfile({ characterPrompt: "", userProfile: "", relationshipSummary: "" });
    assert.deepEqual(profile, defaultCompanionProfile);
});
//# sourceMappingURL=companion.test.js.map