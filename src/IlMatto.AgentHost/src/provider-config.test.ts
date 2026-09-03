import assert from "node:assert/strict";
import { test } from "node:test";
import { formatProviderError, getProviderModelOptions, isGeminiOpenAiEndpoint, normalizeProviderBaseUrl } from "./provider-config.js";

test("normalizes a pasted OpenAI-compatible resource URL", () => {
  assert.equal(normalizeProviderBaseUrl("  'https://generativelanguage.googleapis.com/v1beta/openai/chat/completions/'  "), "https://generativelanguage.googleapis.com/v1beta/openai");
  assert.equal(isGeminiOpenAiEndpoint("https://generativelanguage.googleapis.com/v1beta/openai"), true);
  assert.equal(isGeminiOpenAiEndpoint("https://api.openai.com/v1"), false);
});

test("uses Gemini-compatible request fields", () => {
  const options = getProviderModelOptions("https://generativelanguage.googleapis.com/v1beta/openai");
  assert.equal(options.compat?.supportsStore, false);
  assert.equal(options.compat?.maxTokensField, "max_tokens");
  assert.equal(options.compat?.supportsStrictMode, false);
  assert.equal(options.reasoning, true);
  assert.equal(options.headers?.["x-goog-api-client"], "IlMatto/0.1.0");
});

test("provider diagnostics redact API keys", () => {
  const message = formatProviderError(new Error("401 invalid key AIza123456789012345678901234567890"), "https://generativelanguage.googleapis.com/v1beta/openai", "gemini-3.7-flash");
  assert.doesNotMatch(message, /AIza123/);
  assert.match(message, /Gemini 兼容端点/);
});
