import { createHash } from "node:crypto";
import { existsSync } from "node:fs";
import { mkdir, readFile, stat, writeFile } from "node:fs/promises";
import os from "node:os";
import path from "node:path";
const VISION_ENDPOINT = "https://vision.googleapis.com/v1/images:annotate";
const MAX_INLINE_IMAGE_BYTES = 7_000_000;
const MAX_RESULTS_PER_GROUP = 5;
const CACHE_TTL_MS = 24 * 60 * 60 * 1000;
const DEFAULT_TIMEOUT_MS = 20_000;
const DEFAULT_RATE_LIMIT_PER_MINUTE = 6;
const GOOGLE_CLOUD_SCOPE = "https://www.googleapis.com/auth/cloud-platform";
/**
 * Small REST client for Google Cloud Vision Web Detection. It deliberately
 * accepts only a validated local image path and returns a bounded, redacted
 * result. Authentication and network access remain in ManagerHost; neither
 * the MCP process nor Antigravity receives a token or the raw Google payload.
 */
export class VisionWebDetectionClient {
    enabled;
    apiKey;
    credentialsFile;
    cacheDirectory;
    timeoutMs;
    rateLimitPerMinute;
    fetchImpl;
    now;
    requestTimes = [];
    authClientPromise;
    constructor(options = {}) {
        this.enabled = options.enabled ?? envEnabled(process.env.ILMATTO_WEB_DETECTION_ENABLED, true);
        this.apiKey = options.apiKey?.trim() || process.env.ILMATTO_GOOGLE_VISION_API_KEY?.trim() || undefined;
        this.credentialsFile = options.credentialsFile?.trim() || process.env.GOOGLE_APPLICATION_CREDENTIALS?.trim() || undefined;
        const localAppData = process.env.LOCALAPPDATA ?? path.join(process.env.USERPROFILE ?? os.homedir(), "AppData", "Local");
        this.cacheDirectory = path.resolve(options.cacheDirectory ?? path.join(localAppData, "IlMatto", "vision-cache"));
        this.timeoutMs = Math.max(1_000, options.timeoutMs ?? DEFAULT_TIMEOUT_MS);
        this.rateLimitPerMinute = Math.max(1, options.rateLimitPerMinute ?? numberEnv(process.env.ILMATTO_WEB_DETECTION_RATE_LIMIT, DEFAULT_RATE_LIMIT_PER_MINUTE));
        this.fetchImpl = options.fetchImpl ?? fetch;
        this.now = options.now ?? (() => Date.now());
    }
    async identifyImage(imagePath, _question) {
        if (!this.enabled)
            return emptyResult("disabled", "Web Detection 当前已停用。", false);
        const validatedPath = path.resolve(imagePath);
        const info = await stat(validatedPath).catch(() => undefined);
        if (!info?.isFile())
            return emptyResult("error", "图片附件不存在或不是普通文件。", false, "IMAGE_NOT_FOUND");
        if (info.size > MAX_INLINE_IMAGE_BYTES)
            return emptyResult("too_large", "图片过大，无法以内嵌方式发送给 Web Detection。", false, "IMAGE_TOO_LARGE");
        const bytes = await readFile(validatedPath);
        if (bytes.byteLength > MAX_INLINE_IMAGE_BYTES)
            return emptyResult("too_large", "图片过大，无法以内嵌方式发送给 Web Detection。", false, "IMAGE_TOO_LARGE");
        const imageSha256 = createHash("sha256").update(bytes).digest("hex");
        const cached = await this.readCache(imageSha256);
        if (cached) {
            const { expires_at: _expiresAt, ...result } = cached;
            return { ...result, cached: true };
        }
        const auth = await this.authorizationHeader();
        if (!auth)
            return emptyResult("unavailable", "Google Cloud Vision 凭据不可用。", false, "GOOGLE_AUTH_UNAVAILABLE", imageSha256);
        if (!this.allowRequest())
            return emptyResult("rate_limited", "Web Detection 请求频率已达到当前限制，请稍后重试。", false, "RATE_LIMITED", imageSha256);
        const controller = new AbortController();
        const timer = setTimeout(() => controller.abort(), this.timeoutMs);
        try {
            const url = this.apiKey ? `${VISION_ENDPOINT}?key=${encodeURIComponent(this.apiKey)}` : VISION_ENDPOINT;
            const response = await this.fetchImpl(url, {
                method: "POST",
                headers: { "content-type": "application/json", ...auth },
                body: JSON.stringify({ requests: [{ image: { content: bytes.toString("base64") }, features: [{ type: "WEB_DETECTION" }] }] }),
                signal: controller.signal,
            });
            const payload = await response.json().catch(() => ({}));
            if (!response.ok)
                return this.httpFailure(response.status, payload, imageSha256);
            const annotation = payload.responses?.[0];
            if (annotation?.error)
                return this.apiFailure(annotation.error, imageSha256);
            const result = normalizeWebDetection(annotation?.webDetection, imageSha256);
            await this.writeCache(result);
            return result;
        }
        catch (error) {
            if (error?.name === "AbortError")
                return emptyResult("error", "Web Detection 请求超时。", false, "VISION_TIMEOUT", imageSha256);
            return emptyResult("error", "Web Detection 请求失败。", false, "VISION_REQUEST_FAILED", imageSha256);
        }
        finally {
            clearTimeout(timer);
        }
    }
    allowRequest() {
        const cutoff = this.now() - 60_000;
        while (this.requestTimes.length > 0 && this.requestTimes[0] < cutoff)
            this.requestTimes.shift();
        if (this.requestTimes.length >= this.rateLimitPerMinute)
            return false;
        this.requestTimes.push(this.now());
        return true;
    }
    async authorizationHeader() {
        if (this.apiKey)
            return {};
        if (this.credentialsFile && !existsSync(this.credentialsFile))
            return undefined;
        try {
            if (!this.authClientPromise) {
                this.authClientPromise = import("google-auth-library").then(async ({ GoogleAuth }) => {
                    const auth = new GoogleAuth({ scopes: [GOOGLE_CLOUD_SCOPE] });
                    return auth.getClient();
                });
            }
            const client = await this.authClientPromise;
            // Use the auth client's complete request headers instead of only the
            // bearer token. User ADC credentials require x-goog-user-project to
            // identify the project used for quota and billing.
            const headers = await client.getRequestHeaders(VISION_ENDPOINT);
            const result = {};
            headers.forEach((value, key) => { result[key] = value; });
            return result.authorization ? result : undefined;
        }
        catch {
            return undefined;
        }
    }
    httpFailure(status, payload, imageSha256) {
        if (status === 401 || status === 403)
            return emptyResult("unavailable", "Google Cloud Vision 认证或权限失败。", false, "GOOGLE_AUTH_FAILED", imageSha256);
        if (status === 413)
            return emptyResult("too_large", "图片请求超过 Google Cloud Vision 大小限制。", false, "VISION_PAYLOAD_TOO_LARGE", imageSha256);
        if (status === 429)
            return emptyResult("rate_limited", "Google Cloud Vision 当前配额或请求频率受限。", false, "GOOGLE_RATE_LIMITED", imageSha256);
        const remoteCode = payload.responses?.[0]?.error?.status;
        return emptyResult("error", "Google Cloud Vision 返回了错误。", false, remoteCode || `HTTP_${status}`, imageSha256);
    }
    apiFailure(error, imageSha256) {
        const status = error.status?.toUpperCase();
        if (status === "UNAUTHENTICATED" || status === "PERMISSION_DENIED")
            return emptyResult("unavailable", "Google Cloud Vision 认证或权限失败。", false, "GOOGLE_AUTH_FAILED", imageSha256);
        if (status === "RESOURCE_EXHAUSTED")
            return emptyResult("rate_limited", "Google Cloud Vision 当前配额或请求频率受限。", false, "GOOGLE_RATE_LIMITED", imageSha256);
        return emptyResult("error", "Google Cloud Vision 返回了错误。", false, status || `GOOGLE_${error.code ?? "ERROR"}`, imageSha256);
    }
    async readCache(imageSha256) {
        try {
            const cached = JSON.parse(await readFile(path.join(this.cacheDirectory, `${imageSha256}.json`), "utf8"));
            if (!cached || cached.expires_at <= this.now() || cached.image_sha256 !== imageSha256)
                return undefined;
            // Migrate caches written before source URLs were removed from the
            // Agent-facing result. The old arrays are used only to recover counts.
            const { matching_pages: _matchingPages, full_matches: oldFullMatches, partial_matches: oldPartialMatches, similar_images: oldSimilarImages, match_counts: existingCounts, ...base } = cached;
            return {
                ...base,
                match_counts: existingCounts ?? {
                    full_matches: Array.isArray(oldFullMatches) ? Math.min(oldFullMatches.length, MAX_RESULTS_PER_GROUP) : 0,
                    partial_matches: Array.isArray(oldPartialMatches) ? Math.min(oldPartialMatches.length, MAX_RESULTS_PER_GROUP) : 0,
                    similar_images: Array.isArray(oldSimilarImages) ? Math.min(oldSimilarImages.length, MAX_RESULTS_PER_GROUP) : 0,
                },
            };
        }
        catch {
            return undefined;
        }
    }
    async writeCache(result) {
        if (result.status !== "ok" && result.status !== "no_match" || !result.image_sha256)
            return;
        try {
            await mkdir(this.cacheDirectory, { recursive: true });
            const cached = { ...result, expires_at: this.now() + CACHE_TTL_MS };
            await writeFile(path.join(this.cacheDirectory, `${result.image_sha256}.json`), JSON.stringify(cached, null, 2), "utf8");
        }
        catch { /* Cache is an optimization; never fail the tool call. */ }
    }
}
function normalizeWebDetection(web, imageSha256) {
    if (!web)
        return emptyResult("no_match", "Google Web Detection 未返回匹配结果。", false, undefined, imageSha256);
    const bestGuess = (web.bestGuessLabels ?? []).map((item) => ({ label: cleanText(item.label, 240), language_code: cleanText(item.languageCode, 32) })).filter((item) => item.label);
    const entities = (web.webEntities ?? []).map((item) => ({ name: cleanText(item.description, 240), score: finiteScore(item.score) })).filter((item) => item.name);
    const nestedFullMatches = (web.pagesWithMatchingImages ?? []).flatMap((page) => page.fullMatchingImages ?? []);
    const nestedPartialMatches = (web.pagesWithMatchingImages ?? []).flatMap((page) => page.partialMatchingImages ?? []);
    const fullMatches = uniqueUrls([...web.fullMatchingImages ?? [], ...nestedFullMatches].map((item) => ({ url: sanitizeUrl(item.url), score: finiteScore(item.score) })));
    const partialMatches = uniqueUrls([...web.partialMatchingImages ?? [], ...nestedPartialMatches].map((item) => ({ url: sanitizeUrl(item.url), score: finiteScore(item.score) })));
    const similarImages = uniqueUrls((web.visuallySimilarImages ?? []).map((item) => ({ url: sanitizeUrl(item.url), score: finiteScore(item.score) })));
    const evidenceLevel = fullMatches.length > 0 && entities.length > 0 ? "very_high" : partialMatches.length > 0 && entities.length > 0 ? "high" : entities.length > 0 || bestGuess.length > 0 ? "medium" : similarImages.length > 0 ? "low" : "unknown";
    const hasResult = bestGuess.length > 0 || entities.length > 0 || fullMatches.length > 0 || partialMatches.length > 0 || similarImages.length > 0;
    return {
        status: hasResult ? "ok" : "no_match",
        best_guess: bestGuess.slice(0, MAX_RESULTS_PER_GROUP),
        entities: entities.slice(0, MAX_RESULTS_PER_GROUP),
        match_counts: {
            full_matches: Math.min(fullMatches.length, MAX_RESULTS_PER_GROUP),
            partial_matches: Math.min(partialMatches.length, MAX_RESULTS_PER_GROUP),
            similar_images: Math.min(similarImages.length, MAX_RESULTS_PER_GROUP),
        },
        evidence_level: evidenceLevel,
        image_sha256: imageSha256,
        cached: false,
        warnings: ["结果来自 Google Cloud Vision Web Detection。实体是基于网络相似图片推断的候选线索；score 是未归一化的相关性分数，不是概率。"]
    };
}
function emptyResult(status, warning, cached, errorCode, imageSha256) {
    return { status, best_guess: [], entities: [], match_counts: { full_matches: 0, partial_matches: 0, similar_images: 0 }, evidence_level: "unknown", image_sha256: imageSha256, cached, warnings: warning ? [warning] : [], error_code: errorCode };
}
function sanitizeUrl(value) {
    if (!value)
        return undefined;
    try {
        const url = new URL(value);
        if (url.protocol !== "http:" && url.protocol !== "https:")
            return undefined;
        for (const key of [...url.searchParams.keys()])
            if (/^(utm_|gclid$|fbclid$|msclkid$)/i.test(key))
                url.searchParams.delete(key);
        return url.toString().slice(0, 2_000);
    }
    catch {
        return undefined;
    }
}
function uniqueUrls(items) {
    const seen = new Set();
    const result = [];
    for (const item of items) {
        if (!item.url || seen.has(item.url))
            continue;
        seen.add(item.url);
        result.push(item);
    }
    return result;
}
function cleanText(value, maxLength) {
    return String(value ?? "").replace(/<[^>]*>/g, "").replace(/\s+/g, " ").trim().slice(0, maxLength);
}
function finiteScore(value) {
    return typeof value === "number" && Number.isFinite(value) && value >= 0 ? Math.min(value, 1_000_000) : undefined;
}
function envEnabled(value, fallback) {
    if (!value)
        return fallback;
    return !["0", "false", "off", "no"].includes(value.trim().toLowerCase());
}
function numberEnv(value, fallback) {
    const parsed = Number(value);
    return Number.isFinite(parsed) && parsed > 0 ? Math.floor(parsed) : fallback;
}
//# sourceMappingURL=vision-web-detection.js.map