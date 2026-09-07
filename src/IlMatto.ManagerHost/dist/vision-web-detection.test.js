import assert from "node:assert/strict";
import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import { test } from "node:test";
import { VisionWebDetectionClient } from "./vision-web-detection.js";
test("Web Detection sends only the WEB_DETECTION feature and returns bounded evidence", async () => {
    const root = await mkdtemp(path.join(os.tmpdir(), "ilmatto-vision-test-"));
    const imagePath = path.join(root, "image.png");
    const cacheDirectory = path.join(root, "cache");
    await writeFile(imagePath, Buffer.from("fake-image-bytes"));
    let calls = 0;
    let requestUrl = "";
    let requestBody;
    try {
        const client = new VisionWebDetectionClient({
            apiKey: "local-test-key",
            cacheDirectory,
            fetchImpl: async (input, init) => {
                calls += 1;
                requestUrl = String(input);
                requestBody = JSON.parse(String(init?.body));
                return new Response(JSON.stringify({ responses: [{ webDetection: {
                                bestGuessLabels: [{ label: "Frieren", languageCode: "en" }],
                                webEntities: Array.from({ length: 8 }, (_, index) => ({ description: `entity-${index}`, score: index === 0 ? 5.35 : 0.9 })),
                                pagesWithMatchingImages: [{ pageTitle: "Frieren page", url: "https://example.test/page?utm_source=tracking", fullMatchingImages: [{ url: "https://example.test/full.png" }] }],
                                fullMatchingImages: Array.from({ length: 8 }, (_, index) => ({ url: `https://example.test/full-${index}.png` })),
                                partialMatchingImages: [{ url: "https://example.test/partial.png", score: 0.5 }],
                                visuallySimilarImages: [{ url: "https://example.test/similar.png", score: 0.2 }],
                            } }] }), { status: 200, headers: { "content-type": "application/json" } });
            },
        });
        const result = await client.identifyImage(imagePath, "这个角色是谁？");
        assert.equal(result.status, "ok");
        assert.equal(result.evidence_level, "very_high");
        assert.equal(result.best_guess[0]?.label, "Frieren");
        assert.equal(result.entities.length, 5);
        assert.equal(result.entities[0]?.score, 5.35);
        assert.deepEqual(result.match_counts, { full_matches: 5, partial_matches: 1, similar_images: 1 });
        assert.equal("matching_pages" in result, false);
        assert.equal("full_matches" in result, false);
        assert.equal(result.cached, false);
        assert.equal(calls, 1);
        assert.match(requestUrl, /[?&]key=local-test-key$/);
        assert.deepEqual(requestBody.requests[0].features, [{ type: "WEB_DETECTION" }]);
        assert.equal(requestBody.requests[0].image.content, Buffer.from("fake-image-bytes").toString("base64"));
        assert.doesNotMatch(JSON.stringify(result), /fake-image-bytes|local-test-key|[A-Z]:\\/i);
        const cached = await client.identifyImage(imagePath);
        assert.equal(cached.cached, true);
        assert.equal(calls, 1);
        assert.equal((await readFile(path.join(cacheDirectory, `${result.image_sha256}.json`), "utf8")).includes("expires_at"), true);
    }
    finally {
        await rm(root, { recursive: true, force: true });
    }
});
test("Web Detection remains optional and reports unavailable, disabled, and rate-limited states", async () => {
    const root = await mkdtemp(path.join(os.tmpdir(), "ilmatto-vision-status-"));
    const imageOne = path.join(root, "one.png");
    const imageTwo = path.join(root, "two.png");
    await writeFile(imageOne, "one");
    await writeFile(imageTwo, "two");
    try {
        const unavailable = new VisionWebDetectionClient({ credentialsFile: path.join(root, "missing-credentials.json"), cacheDirectory: path.join(root, "unavailable-cache") });
        assert.equal((await unavailable.identifyImage(imageOne)).status, "unavailable");
        const disabled = new VisionWebDetectionClient({ enabled: false, apiKey: "test", cacheDirectory: path.join(root, "disabled-cache") });
        assert.equal((await disabled.identifyImage(imageOne)).status, "disabled");
        const limited = new VisionWebDetectionClient({ apiKey: "test", rateLimitPerMinute: 1, cacheDirectory: path.join(root, "limited-cache"), fetchImpl: async () => new Response(JSON.stringify({ responses: [{ webDetection: {} }] }), { status: 200 }) });
        assert.equal((await limited.identifyImage(imageOne)).status, "no_match");
        assert.equal((await limited.identifyImage(imageTwo)).status, "rate_limited");
    }
    finally {
        await rm(root, { recursive: true, force: true });
    }
});
test("Web Detection maps HTTP failures without exposing raw responses", async () => {
    const root = await mkdtemp(path.join(os.tmpdir(), "ilmatto-vision-errors-"));
    const imagePath = path.join(root, "image.png");
    await writeFile(imagePath, "image");
    try {
        const client = new VisionWebDetectionClient({ apiKey: "test", cacheDirectory: path.join(root, "cache"), fetchImpl: async () => new Response(JSON.stringify({ error: { message: "secret raw response" } }), { status: 429 }) });
        const result = await client.identifyImage(imagePath);
        assert.equal(result.status, "rate_limited");
        assert.equal(result.error_code, "GOOGLE_RATE_LIMITED");
        assert.doesNotMatch(JSON.stringify(result), /secret raw response|test/);
    }
    finally {
        await rm(root, { recursive: true, force: true });
    }
});
//# sourceMappingURL=vision-web-detection.test.js.map