import type { Model } from "@mariozechner/pi-ai";

/**
 * The desktop settings field is a provider base URL, not a concrete
 * /chat/completions resource. Accepting the concrete resource as input is
 * useful because Google's documentation shows that URL, but passing it to the
 * OpenAI SDK would append /chat/completions a second time.
 */
export function normalizeProviderBaseUrl(value: string): string {
  const unquoted = value.trim().replace(/^[`"']|[`"']$/g, "");
  const withoutTrailingSlash = unquoted.replace(/\/+$/, "");
  return withoutTrailingSlash.replace(/\/chat\/completions$/i, "");
}

export function isGeminiOpenAiEndpoint(value: string): boolean {
  const normalized = normalizeProviderBaseUrl(value).toLowerCase();
  return /^https?:\/\/generativelanguage\.googleapis\.com\/v1(?:beta)?\/openai(?:\/|$)/.test(normalized);
}

export function getProviderModelOptions(baseUrl: string): {
  compat?: NonNullable<Model<"openai-completions">["compat"]>;
  headers?: Record<string, string>;
  reasoning: boolean;
  contextWindow: number;
  maxTokens: number;
} {
  if (!isGeminiOpenAiEndpoint(baseUrl)) {
    return { reasoning: false, contextWindow: 128_000, maxTokens: 16_384 };
  }

  // Pi auto-detects a custom URL as a standard OpenAI endpoint. Gemini's
  // compatibility endpoint does not accept several OpenAI-only fields (most
  // notably store, developer-role messages, max_completion_tokens and strict
  // tool schemas), so opt into the documented compatibility shape explicitly.
  return {
    reasoning: true,
    contextWindow: 1_048_576,
    maxTokens: 65_536,
    compat: {
      supportsStore: false,
      supportsDeveloperRole: false,
      supportsReasoningEffort: true,
      supportsUsageInStreaming: true,
      maxTokensField: "max_tokens",
      requiresToolResultName: false,
      requiresAssistantAfterToolResult: false,
      requiresThinkingAsText: false,
      requiresReasoningContentOnAssistantMessages: false,
      supportsStrictMode: false,
    },
    headers: { "x-goog-api-client": "IlMatto/0.1.0" },
  };
}

export function formatProviderError(error: unknown, baseUrl: string, modelId: string): string {
  const candidate = error as any;
  const status = typeof candidate?.status === "number" ? ` HTTP ${candidate.status}` : "";
  const message = (error instanceof Error ? error.message : typeof error === "string" ? error : "未知错误")
    .replace(/(sk-[A-Za-z0-9_-]{8,}|AIza[\w-]{20,})/g, "[已脱敏]");
  const rawBody = candidate?.error ?? candidate?.response?.data;
  let detail = "";
  if (rawBody && typeof rawBody === "object") {
    try { detail = JSON.stringify(rawBody); } catch { detail = ""; }
  } else if (typeof rawBody === "string") {
    detail = rawBody;
  }
  // Never include the API key in diagnostics. Provider error bodies normally
  // do not contain it, but the defensive redaction also covers proxy errors.
  detail = detail.replace(/(sk-[A-Za-z0-9_-]{8,}|AIza[\w-]{20,})/g, "[已脱敏]");
  const lines = [`模型请求失败${status}：${message}`];
  if (detail && detail !== message) lines.push(`服务端详情：${detail}`);
  if (isGeminiOpenAiEndpoint(baseUrl)) {
    lines.push(`Gemini 兼容端点：${normalizeProviderBaseUrl(baseUrl)}，模型：${modelId}`);
    lines.push("请确认 Base URL 只填写到 /v1beta/openai（不要填写 /chat/completions），并使用 Google AI Studio API Key。可先用 gemini-3.7-flash 或 gemini-2.5-flash 验证。" );
  }
  return lines.join("\n");
}
