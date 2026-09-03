import assert from "node:assert/strict";
import { mkdtemp, readFile } from "node:fs/promises";
import http from "node:http";
import os from "node:os";
import path from "node:path";
import { test } from "node:test";
import { OpenAICompatibleCoordinator } from "./coordinator.js";
test("OpenAI-compatible coordinator falls back, repairs schema, persists no credential, and reports cache metrics", async () => {
    const directory = await mkdtemp(path.join(os.tmpdir(), "ilmatto-coordinator-"));
    const previous = process.env.ILMATTO_COORDINATOR_SESSION_DIR;
    process.env.ILMATTO_COORDINATOR_SESSION_DIR = directory;
    const bodies = [];
    const authorizations = [];
    const server = http.createServer(async (request, response) => {
        let body = "";
        for await (const chunk of request)
            body += chunk;
        bodies.push(JSON.parse(body));
        authorizations.push(String(request.headers.authorization ?? ""));
        response.setHeader("content-type", "application/json");
        if (bodies.length === 1) {
            response.statusCode = 400;
            response.end(JSON.stringify({ error: { message: "response_format json_schema unsupported" } }));
        }
        else if (bodies.length === 2) {
            response.end(JSON.stringify({ choices: [{ message: { content: "not valid json" } }] }));
        }
        else {
            response.end(JSON.stringify({
                choices: [{ message: { content: JSON.stringify({ schemaVersion: 1, action: "respond", message: "你好" }) } }],
                usage: { prompt_tokens: 1234, prompt_tokens_details: { cached_tokens: 1024 }, context_window: 128000 },
            }));
        }
    });
    await new Promise((resolve) => server.listen(0, "127.0.0.1", resolve));
    try {
        const address = server.address();
        assert.ok(address && typeof address === "object");
        const coordinator = new OpenAICompatibleCoordinator({
            provider: "openai_compatible", baseUrl: `http://127.0.0.1:${address.port}/v1`, modelId: "test-model", apiKey: "sk-test-secret", timeoutSeconds: 10,
        }, "session-1");
        const turn = await coordinator.ask("普通问题");
        assert.equal(turn.action.action, "respond");
        assert.equal(turn.cacheReadTokens, 1024);
        assert.equal(turn.contextTokens, 1234);
        assert.equal(bodies.length, 3);
        assert.equal(bodies[0].response_format.type, "json_schema");
        assert.equal(bodies[1].response_format.type, "json_object");
        assert.ok(authorizations.every((value) => value === "Bearer sk-test-secret"));
        const stored = await readFile(coordinator.sessionRef, "utf8");
        assert.doesNotMatch(stored, /sk-test-secret/);
        assert.match(stored, /普通问题/);
    }
    finally {
        await new Promise((resolve) => server.close(() => resolve()));
        if (previous === undefined)
            delete process.env.ILMATTO_COORDINATOR_SESSION_DIR;
        else
            process.env.ILMATTO_COORDINATOR_SESSION_DIR = previous;
    }
});
test("OpenAI-compatible coordinator forwards streamed reasoning and response text", async () => {
    const directory = await mkdtemp(path.join(os.tmpdir(), "ilmatto-coordinator-stream-"));
    const previous = process.env.ILMATTO_COORDINATOR_SESSION_DIR;
    process.env.ILMATTO_COORDINATOR_SESSION_DIR = directory;
    const server = http.createServer(async (_request, response) => {
        response.setHeader("content-type", "text/event-stream");
        response.write(`data: ${JSON.stringify({ choices: [{ delta: { reasoning_content: "先判断问题。" } }] })}\n\n`);
        response.write(`data: ${JSON.stringify({ choices: [{ delta: { content: '{"schemaVersion":1,"action":"respond","message":"你好' } }] })}\n\n`);
        response.write(`data: ${JSON.stringify({ choices: [{ delta: { content: '，世界！"}' } }], usage: { prompt_tokens: 12, prompt_tokens_details: { cached_tokens: 5 } } })}\n\n`);
        response.end("data: [DONE]\n\n");
    });
    await new Promise((resolve) => server.listen(0, "127.0.0.1", resolve));
    try {
        const address = server.address();
        assert.ok(address && typeof address === "object");
        const events = [];
        const coordinator = new OpenAICompatibleCoordinator({
            provider: "openai_compatible", baseUrl: `http://127.0.0.1:${address.port}/v1`, modelId: "test-model", apiKey: "sk-test-secret", timeoutSeconds: 10,
        }, "stream-session");
        const turn = await coordinator.ask("普通问题", (event) => events.push(event));
        assert.equal(turn.action.message, "你好，世界！");
        assert.deepEqual(events.map((event) => event.kind), ["thinking", "text", "text"]);
        assert.equal(events[0].text, "先判断问题。");
        assert.equal(events.slice(1).map((event) => event.text).join(""), "你好，世界！");
        assert.equal(turn.cacheReadTokens, 5);
    }
    finally {
        await new Promise((resolve) => server.close(() => resolve()));
        if (previous === undefined)
            delete process.env.ILMATTO_COORDINATOR_SESSION_DIR;
        else
            process.env.ILMATTO_COORDINATOR_SESSION_DIR = previous;
    }
});
//# sourceMappingURL=coordinator.test.js.map