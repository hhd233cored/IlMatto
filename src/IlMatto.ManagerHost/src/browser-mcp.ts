import net from "node:net";
import os from "node:os";
import path from "node:path";
import process from "node:process";
import { randomUUID } from "node:crypto";
import { mkdir, stat } from "node:fs/promises";
import { chromium, type Browser, type Locator, type Page } from "playwright-core";
import type { BrowserMcpOperation, BrowserPermissionName, BrowserRequest } from "./protocol.js";

type JsonRpcMessage = { jsonrpc?: string; id?: string | number | null; method?: string; params?: any };
type Content = { type: "text"; text: string } | { type: "image"; data: string; mimeType: string };
type BrowserRpcResponse = { ok: boolean; data?: any; error?: { code: string; message: string } };

const pipeArg = argument("--pipe") ?? process.env.ILMATTO_MANAGER_PIPE;
const sessionId = argument("--session-id") ?? process.env.ILMATTO_MANAGER_SESSION_ID;
if (!pipeArg || !sessionId) {
  console.error("ilmatto-browser MCP requires --pipe <manager-pipe> and --session-id <session-id>");
  process.exit(2);
}

class BrowserRpcClient {
  private readonly socket: net.Socket;
  private buffer = "";
  private pending = new Map<string, { resolve: (value: BrowserRpcResponse) => void; reject: (error: Error) => void; timer: NodeJS.Timeout }>();
  private connected = false;

  constructor(pipeName: string) {
    const pipePath = pipeName.startsWith("\\\\.\\pipe\\") ? pipeName : `\\\\.\\pipe\\${pipeName}`;
    this.socket = net.createConnection(pipePath);
    this.socket.setEncoding("utf8");
    this.socket.on("connect", () => { this.connected = true; });
    this.socket.on("data", (chunk: string) => this.onData(chunk));
    this.socket.on("error", (error) => this.failAll(error));
    this.socket.on("close", () => { this.connected = false; this.failAll(new Error("ManagerHost 连接已关闭。")); });
  }

  async ready(): Promise<void> {
    await new Promise<void>((resolve, reject) => {
      const timer = setTimeout(() => reject(new Error("连接 ManagerHost 超时。")), 8_000);
      if (this.socket.readyState === "open") { clearTimeout(timer); resolve(); return; }
      this.socket.once("connect", () => { clearTimeout(timer); resolve(); });
      this.socket.once("error", (error) => { clearTimeout(timer); reject(error); });
    });
  }

  request(operation: BrowserMcpOperation, args: Record<string, unknown> = {}): Promise<BrowserRpcResponse> {
    if (!this.connected && this.socket.readyState !== "open") return Promise.reject(new Error("ManagerHost 尚未连接。"));
    const requestId = `browser-${randomUUID()}`;
    const message: BrowserRequest = {
      type: "browser_request", sessionId: sessionId!, requestId, operation,
      url: typeof args.url === "string" ? args.url : undefined,
      ref: typeof args.ref === "string" ? args.ref : undefined,
      text: typeof args.text === "string" ? args.text : undefined,
      key: typeof args.key === "string" ? args.key : undefined,
      direction: isDirection(args.direction) ? args.direction : undefined,
      amount: typeof args.amount === "number" ? args.amount : undefined,
      details: typeof args.details === "string" ? args.details : undefined,
      permission: isPermission(args.permission) ? args.permission : undefined,
      path: typeof args.path === "string" ? args.path : undefined,
      expression: typeof args.expression === "string" ? args.expression : undefined,
      x: typeof args.x === "number" ? args.x : undefined,
      y: typeof args.y === "number" ? args.y : undefined,
      action: typeof args.action === "string" ? args.action : undefined,
      button: isMouseButton(args.button) ? args.button : undefined,
      write: typeof args.write === "boolean" ? args.write : undefined,
      page: isRecord(args.page) ? args.page as BrowserRequest["page"] : undefined,
    };
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => { this.pending.delete(requestId); reject(new Error("BrowserHost 请求超时。")); }, 180_000);
      this.pending.set(requestId, { resolve, reject, timer });
      this.socket.write(`${JSON.stringify(message)}\n`);
    });
  }

  async close(): Promise<void> {
    // Browser lifecycle belongs to ManagerHost.  The MCP process can be
    // restarted by Antigravity without taking the shared headed browser down.
    // Ending this socket is enough to detach the short-lived MCP transport.
    this.socket.end();
  }

  private onData(chunk: string): void {
    this.buffer += chunk;
    let newline = this.buffer.indexOf("\n");
    while (newline >= 0) {
      const line = this.buffer.slice(0, newline).trim();
      this.buffer = this.buffer.slice(newline + 1);
      newline = this.buffer.indexOf("\n");
      if (!line) continue;
      let message: any;
      try { message = JSON.parse(line); } catch { continue; }
      if (message.type !== "browser_action_result" || typeof message.requestId !== "string") continue;
      const pending = this.pending.get(message.requestId);
      if (!pending) continue;
      clearTimeout(pending.timer); this.pending.delete(message.requestId);
      pending.resolve({ ok: message.ok === true, data: message.data, error: message.error });
    }
  }

  private failAll(error: Error): void {
    for (const pending of this.pending.values()) { clearTimeout(pending.timer); pending.reject(error); }
    this.pending.clear();
  }
}

class BrowserMcpSession {
  private browser?: Browser;
  private cdpEndpoint?: string;
  private refs = new Map<string, Locator>();
  private snapshotPage?: Page;
  private lastHostState?: string;
  private snapshotRequired = false;

  constructor(private readonly host: BrowserRpcClient) {}

  async call(name: string, args: Record<string, unknown>): Promise<{ content: Content[]; structuredContent?: unknown }> {
    switch (name) {
      case "browser_tabs": return this.tabs();
      case "browser_navigate": return this.navigate(args);
      case "browser_snapshot": return this.snapshot();
      case "browser_click": return this.click(args);
      case "browser_fill": return this.fill(args);
      case "browser_press": return this.press(args);
      case "browser_scroll": return this.scroll(args);
      case "browser_screenshot": return this.screenshot(args);
      case "browser_upload": return this.upload(args);
      case "browser_download": return this.download(args);
      case "browser_evaluate": return this.evaluate(args);
      case "browser_mouse": return this.mouse(args);
      case "browser_keyboard": return this.keyboard(args);
      default: throw new BrowserMcpError("BROWSER_TOOL_NOT_FOUND", `不支持的 Browser MCP 工具：${name}`);
    }
  }

  async dispose(): Promise<void> {
    // Do not stop the shared browser here. Antigravity may recycle this MCP
    // process while the Manager session and its BrowserController remain
    // alive. Detach Playwright's client connection (without browser.close(),
    // which sends Browser.close over CDP) so the MCP process can exit cleanly
    // while the remote headed Chrome remains available.
    this.detachBrowserConnection();
  }

  private async ensureBrowser(): Promise<Browser> {
    const connected = this.browser?.isConnected() === true;
    // A connected Playwright object is not sufficient evidence that the
    // headed window is still healthy. Ask ManagerHost for a state/health
    // refresh before reusing it; this also lets the host restore a window
    // after Antigravity has recycled the MCP process.
    let response = await this.host.request(connected ? "state" : "start");
    if (!response.ok) throw responseError(response);
    let endpoint = response.data?.cdpEndpoint ?? response.data?.data?.cdpEndpoint;
    // A user closing the last browser window is reported as Stopped. The
    // Playwright client can remain briefly connected while the host is
    // cleaning up that process, so treat both terminal states as a signal to
    // detach and request a fresh singleton browser instead of returning a
    // misleading missing-CDP error.
    if (connected && (response.data?.state === "Error" || response.data?.state === "Stopped")) {
      this.detachBrowserConnection();
      response = await this.host.request("start");
      if (!response.ok) throw responseError(response);
      endpoint = response.data?.cdpEndpoint ?? response.data?.data?.cdpEndpoint;
    }
    if (typeof endpoint !== "string" || !endpoint) throw new BrowserMcpError("BROWSER_CDP_CONNECT_FAILED", "BrowserHost 未返回 CDP 地址。" );
    if (this.browser?.isConnected() && this.cdpEndpoint === endpoint) return this.browser;
    try {
      this.detachBrowserConnection();
      this.browser = await chromium.connectOverCDP(endpoint);
      this.cdpEndpoint = endpoint;
      return this.browser;
    } catch (error) {
      throw new BrowserMcpError("BROWSER_CDP_CONNECT_FAILED", `Playwright 无法连接 Chrome CDP：${error instanceof Error ? error.message : "未知错误"}`);
    }
  }

  private detachBrowserConnection(): void {
    const browser = this.browser;
    this.browser = undefined;
    this.cdpEndpoint = undefined;
    this.refs.clear();
    this.snapshotPage = undefined;
    this.snapshotRequired = false;
    if (!browser) return;
    // Browser._connection is an intentionally private Playwright detail, but
    // this is the only pinned-client operation that closes the MCP transport
    // without sending the remote Browser.close command. Keep the cast local
    // and fail silently if a future Playwright version changes the internals.
    const connection = (browser as unknown as { _connection?: { close?: (cause?: string) => void } })._connection;
    try { connection?.close?.("Browser MCP detached"); } catch { /* already detached */ }
  }

  private async page(): Promise<Page> {
    const browser = await this.ensureBrowser();
    const context = browser.contexts()[0] ?? await browser.newContext();
    const pages = context.pages();
    return pages[pages.length - 1] ?? await context.newPage();
  }

  private async tabs(): Promise<{ content: Content[]; structuredContent: unknown }> {
    const browser = await this.ensureBrowser();
    const tabs: Array<{ index: number; url: string; title: string }> = [];
    for (const context of browser.contexts()) {
      for (const page of context.pages()) tabs.push({ index: tabs.length, url: page.url(), title: await page.title().catch(() => "") });
    }
    return result(tabs);
  }

  private async navigate(args: Record<string, unknown>): Promise<{ content: Content[]; structuredContent: unknown }> {
    const url = typeof args.url === "string" ? args.url.trim() : "";
    if (!/^https?:\/\//i.test(url)) throw new BrowserMcpError("BROWSER_INVALID_URL", "browser_navigate 只允许 http/https URL。" );
    await this.authorize("navigate", `browser_navigate\n导航到：${url}`);
    const page = await this.page();
    await page.goto(url, { waitUntil: "domcontentloaded", timeout: 60_000 });
    return await this.afterPageChange(page);
  }

  private async snapshot(): Promise<{ content: Content[]; structuredContent: unknown }> {
    const page = await this.page();
    const body = await page.locator("body").innerText({ timeout: 5_000 }).catch(() => "");
    const title = await page.title().catch(() => "");
    const controls = page.locator("button, a, input, textarea, select, [role]");
    const count = Math.min(await controls.count(), 100);
    this.refs.clear(); this.snapshotPage = page;
    const lines = [`URL: ${page.url()}`, `TITLE: ${title}`];
    for (let index = 0; index < count; index++) {
      const locator = controls.nth(index);
      const tag = await locator.evaluate((element) => element.tagName.toLowerCase()).catch(() => "element");
      const role = await locator.getAttribute("role").catch(() => null);
      const label = await locator.getAttribute("aria-label").catch(() => null);
      const text = (label || await locator.innerText().catch(() => "") || await locator.getAttribute("placeholder").catch(() => "") || "").replace(/\s+/g, " ").trim().slice(0, 160);
      const ref = `e${index + 1}`;
      this.refs.set(ref, locator);
      lines.push(`[${ref}] ${role || tag}${text ? ` "${text}"` : ""}`);
    }
    if (body) lines.push("", body.slice(0, 12_000));
    await this.reportPageState(page, `${title}\n${body.slice(0, 4_000)}`);
    // The snapshot just collected fresh refs, so it is the acknowledgement
    // required after an automatic human-verification resume.
    this.snapshotRequired = false;
    return result({ url: page.url(), title, snapshot: lines.join("\n"), refs: [...this.refs.keys()] });
  }

  private async click(args: Record<string, unknown>): Promise<{ content: Content[]; structuredContent: unknown }> {
    const locator = this.resolveRef(args);
    await this.authorize("click", `browser_click\n点击元素：${String(args.ref)}`);
    await locator.click({ timeout: 10_000 });
    return await this.afterPageChange(this.snapshotPage ?? await this.page());
  }

  private async fill(args: Record<string, unknown>): Promise<{ content: Content[]; structuredContent: unknown }> {
    const locator = this.resolveRef(args);
    const text = typeof args.text === "string" ? args.text : "";
    await this.authorize("fill", `browser_fill\n填写元素：${String(args.ref)}\n内容长度：${text.length}`);
    await locator.fill(text, { timeout: 10_000 });
    return result({ ref: args.ref, filled: true, length: text.length });
  }

  private async press(args: Record<string, unknown>): Promise<{ content: Content[]; structuredContent: unknown }> {
    const locator = this.resolveRef(args);
    const key = typeof args.key === "string" ? args.key.trim() : "Enter";
    if (!/^(Enter|Tab|Escape|Arrow(?:Up|Down|Left|Right)|Page(?:Up|Down)|Backspace|Delete|[A-Za-z0-9])$/i.test(key))
      throw new BrowserMcpError("BROWSER_INVALID_KEY", "browser_press 只允许常用按键。" );
    await this.authorize("press", `browser_press\n按键：${key}\n元素：${String(args.ref)}`);
    await locator.press(key, { timeout: 10_000 });
    return await this.afterPageChange(this.snapshotPage ?? await this.page());
  }

  private async scroll(args: Record<string, unknown>): Promise<{ content: Content[]; structuredContent: unknown }> {
    const page = await this.page();
    const direction = isDirection(args.direction) ? args.direction : "down";
    const amount = Math.min(Math.max(Math.abs(Number(args.amount ?? 640)) || 640, 40), 2_000);
    await this.authorize("scroll", `browser_scroll\n方向：${direction}\n距离：${amount}`, false);
    if (direction === "up") await page.keyboard.press("PageUp");
    else if (direction === "down") await page.keyboard.press("PageDown");
    else await page.mouse.wheel(direction === "left" ? -amount : amount, 0);
    return result({ direction, amount, url: page.url() });
  }

  private async screenshot(args: Record<string, unknown>): Promise<{ content: Content[]; structuredContent: unknown }> {
    const page = await this.page();
    await this.authorize("screenshot", "browser_screenshot", false);
    const image = await page.screenshot({ type: "png", fullPage: args.fullPage === true });
    await this.reportPageState(page);
    return { content: [{ type: "image", data: image.toString("base64"), mimeType: "image/png" }], structuredContent: { url: page.url(), mimeType: "image/png" } };
  }

  private async upload(args: Record<string, unknown>): Promise<{ content: Content[]; structuredContent: unknown }> {
    const locator = this.resolveRef(args);
    const filePath = typeof args.path === "string" ? args.path.trim() : "";
    if (!filePath) throw new BrowserMcpError("BROWSER_FILE_REQUIRED", "browser_upload 需要文件路径。");
    const absolutePath = path.resolve(filePath);
    const file = await stat(absolutePath).catch(() => undefined);
    if (!file?.isFile()) throw new BrowserMcpError("BROWSER_FILE_NOT_FOUND", `找不到要上传的文件：${absolutePath}`);
    await this.authorize("upload", `browser_upload\n元素：${String(args.ref)}\n文件：${absolutePath}`);
    await locator.setInputFiles(absolutePath, { timeout: 10_000 });
    await this.reportPageState(this.snapshotPage ?? await this.page());
    return result({ ref: args.ref, uploaded: true, path: absolutePath, size: file.size });
  }

  private async download(args: Record<string, unknown>): Promise<{ content: Content[]; structuredContent: unknown }> {
    const locator = this.resolveRef(args);
    const page = this.snapshotPage ?? await this.page();
    await this.authorize("download", `browser_download\n元素：${String(args.ref)}`);
    const downloadPromise = page.waitForEvent("download", { timeout: 60_000 });
    await locator.click({ timeout: 10_000 });
    const download = await downloadPromise;
    const failure = await download.failure();
    if (failure) throw new BrowserMcpError("BROWSER_DOWNLOAD_FAILED", failure);
    const suggested = safeFilename(download.suggestedFilename());
    const requestedPath = typeof args.path === "string" ? args.path.trim() : "";
    const destination = path.resolve(requestedPath || path.join(os.homedir(), "Downloads", suggested));
    await mkdir(path.dirname(destination), { recursive: true });
    await download.saveAs(destination);
    await this.reportPageState(page);
    return result({ ref: args.ref, downloaded: true, path: destination, filename: suggested });
  }

  private async evaluate(args: Record<string, unknown>): Promise<{ content: Content[]; structuredContent: unknown }> {
    const expression = typeof args.expression === "string" ? args.expression.trim() : "";
    if (!expression) throw new BrowserMcpError("BROWSER_EXPRESSION_REQUIRED", "browser_evaluate 需要 JavaScript 表达式。");
    if (expression.length > 20_000) throw new BrowserMcpError("BROWSER_EXPRESSION_TOO_LARGE", "browser_evaluate 表达式不能超过 20000 个字符。");
    await this.authorize("evaluate", `browser_evaluate\n表达式长度：${expression.length}`);
    const page = await this.page();
    const value = await page.evaluate(expression);
    return result({ url: page.url(), value: jsonSafe(value) });
  }

  private async mouse(args: Record<string, unknown>): Promise<{ content: Content[]; structuredContent: unknown }> {
    const x = finiteCoordinate(args.x);
    const y = finiteCoordinate(args.y);
    const action = args.action === "move" || args.action === "click" || args.action === "dblclick" || args.action === "down" || args.action === "up" ? args.action : "click";
    const button = isMouseButton(args.button) ? args.button : "left";
    await this.authorize("coordinate", `browser_mouse\n动作：${action}\n坐标：${x},${y}`);
    const page = await this.page();
    if (action === "move") await page.mouse.move(x, y);
    else if (action === "click") await page.mouse.click(x, y, { button });
    else if (action === "dblclick") await page.mouse.dblclick(x, y, { button });
    else if (action === "down") { await page.mouse.move(x, y); await page.mouse.down({ button }); }
    else { await page.mouse.move(x, y); await page.mouse.up({ button }); }
    await this.reportPageState(page);
    return result({ action, x, y, button, url: page.url() });
  }

  private async keyboard(args: Record<string, unknown>): Promise<{ content: Content[]; structuredContent: unknown }> {
    const action = args.action === "press" || args.action === "type" || args.action === "insertText" ? args.action : "press";
    const key = typeof args.key === "string" ? args.key.trim() : "Enter";
    const text = typeof args.text === "string" ? args.text : "";
    if (action === "press" && (!key || key.length > 100 || /[\u0000-\u001F]/.test(key)))
      throw new BrowserMcpError("BROWSER_INVALID_KEY", "browser_keyboard.press 需要有效的 Playwright 按键或组合键。");
    if ((action === "type" || action === "insertText") && text.length > 20_000)
      throw new BrowserMcpError("BROWSER_TEXT_TOO_LARGE", "browser_keyboard 文本不能超过 20000 个字符。");
    await this.authorize("coordinate", `browser_keyboard\n动作：${action}`);
    const page = await this.page();
    if (action === "press") await page.keyboard.press(key);
    else if (action === "type") await page.keyboard.type(text);
    else await page.keyboard.insertText(text);
    await this.reportPageState(page);
    return result({ action, key: action === "press" ? key : undefined, length: text.length, url: page.url() });
  }

  private resolveRef(args: Record<string, unknown>): Locator {
    const ref = typeof args.ref === "string" ? args.ref.trim() : "";
    if (!/^e\d+$/.test(ref) || !this.refs.has(ref)) throw new BrowserMcpError("BROWSER_INVALID_REF", "无效或已过期的元素 ref，请先调用 browser_snapshot。" );
    return this.refs.get(ref)!;
  }

  private async authorize(permission: BrowserPermissionName, details: string, write = true): Promise<void> {
    const state = await this.host.request("state");
    if (!state.ok) throw responseError(state);
    const currentState = typeof state.data?.state === "string" ? state.data.state : undefined;
    if (write && currentState === "AgentControlled" && (this.snapshotRequired || (this.lastHostState && this.lastHostState !== "AgentControlled"))) {
      this.refs.clear();
      this.snapshotRequired = true;
      this.lastHostState = currentState;
      throw new BrowserMcpError("BROWSER_SNAPSHOT_REQUIRED", "人工验证刚刚完成，请先重新调用 browser_snapshot 再执行写操作。" );
    }
    this.lastHostState = currentState ?? this.lastHostState;
    const response = await this.host.request("authorize", { details, permission, write });
    if (!response.ok) throw responseError(response);
  }

  private async afterPageChange(page: Page): Promise<{ content: Content[]; structuredContent: unknown }> {
    await page.waitForLoadState("domcontentloaded", { timeout: 5_000 }).catch(() => undefined);
    const title = await page.title().catch(() => "");
    await this.reportPageState(page);
    return result({ url: page.url(), title });
  }

  private async reportPageState(page: Page, textHint?: string): Promise<void> {
    const text = textHint ?? await page.locator("body").innerText({ timeout: 2_000 }).catch(() => "");
    const response = await this.host.request("page_state", { page: { url: page.url(), title: await page.title().catch(() => ""), text: text.slice(0, 8_000) } });
    if (!response.ok) throw responseError(response);
    const nextState = typeof response.data?.state === "string" ? response.data.state : undefined;
    if (nextState === "AgentControlled" && this.lastHostState && this.lastHostState !== "AgentControlled") {
      this.refs.clear();
      this.snapshotRequired = true;
    }
    this.lastHostState = nextState ?? this.lastHostState;
  }
}

class BrowserMcpError extends Error { constructor(readonly code: string, message: string) { super(message); } }

const host = new BrowserRpcClient(pipeArg!);
const browserSession = new BrowserMcpSession(host);
let inputBuffer = "";
process.stdin.setEncoding("utf8");
process.stdin.on("data", (chunk) => {
  inputBuffer += chunk;
  let newline = inputBuffer.indexOf("\n");
  while (newline >= 0) {
    const line = inputBuffer.slice(0, newline).trim(); inputBuffer = inputBuffer.slice(newline + 1); newline = inputBuffer.indexOf("\n");
    if (line) void handleMessage(line);
  }
});
process.stdin.on("end", () => void browserSession.dispose().finally(() => host.close()));
void host.ready().catch((error) => { console.error(error instanceof Error ? error.message : error); process.exitCode = 1; });

async function handleMessage(line: string): Promise<void> {
  let request: JsonRpcMessage;
  try { request = JSON.parse(line); } catch { return; }
  if (request.method === "notifications/initialized" || request.method === "notifications/cancelled") return;
  if (request.id === undefined || request.id === null) return;
  try {
    if (request.method === "initialize") {
      send({ jsonrpc: "2.0", id: request.id, result: { protocolVersion: "2024-11-05", capabilities: { tools: { listChanged: false } }, serverInfo: { name: "ilmatto-browser", version: "1.0.0" } } });
      return;
    }
    if (request.method === "ping") { send({ jsonrpc: "2.0", id: request.id, result: {} }); return; }
    if (request.method === "tools/list") { send({ jsonrpc: "2.0", id: request.id, result: { tools: toolDefinitions() } }); return; }
    if (request.method === "tools/call") {
      const name = String(request.params?.name ?? "");
      const result = await browserSession.call(name, isRecord(request.params?.arguments) ? request.params.arguments : {});
      send({ jsonrpc: "2.0", id: request.id, result });
      return;
    }
    send({ jsonrpc: "2.0", id: request.id, error: { code: -32601, message: `Unsupported MCP method: ${request.method}` } });
  } catch (error) {
    const payload = { code: errorCode(error), message: error instanceof Error ? error.message : "Browser MCP 请求失败。" };
    send({ jsonrpc: "2.0", id: request.id, result: { isError: true, content: [{ type: "text", text: JSON.stringify(payload) }], structuredContent: payload } });
  }
}

function toolDefinitions(): unknown[] {
  return [
    { name: "browser_tabs", description: "列出专用浏览器中的标签页。首次调用时按需启动独立 Chrome。", inputSchema: { type: "object", additionalProperties: false, properties: {} } },
    { name: "browser_navigate", description: "导航到一个 http/https URL；是否允许由 Browser MCP 设置控制。", inputSchema: { type: "object", additionalProperties: false, required: ["url"], properties: { url: { type: "string", minLength: 1 } } } },
    { name: "browser_snapshot", description: "获取当前页面的结构化文本和可操作元素 ref。", inputSchema: { type: "object", additionalProperties: false, properties: {} } },
    { name: "browser_click", description: "点击最近一次 browser_snapshot 返回的元素 ref；是否允许由设置控制。", inputSchema: { type: "object", additionalProperties: false, required: ["ref"], properties: { ref: { type: "string", pattern: "^e[0-9]+$" } } } },
    { name: "browser_fill", description: "填写最近一次 browser_snapshot 返回的输入元素 ref；是否允许由设置控制。", inputSchema: { type: "object", additionalProperties: false, required: ["ref", "text"], properties: { ref: { type: "string", pattern: "^e[0-9]+$" }, text: { type: "string" } } } },
    { name: "browser_press", description: "在元素 ref 上按常用按键；是否允许由设置控制。", inputSchema: { type: "object", additionalProperties: false, required: ["ref", "key"], properties: { ref: { type: "string", pattern: "^e[0-9]+$" }, key: { type: "string" } } } },
    { name: "browser_scroll", description: "滚动当前页面。", inputSchema: { type: "object", additionalProperties: false, properties: { direction: { type: "string", enum: ["up", "down", "left", "right"], default: "down" }, amount: { type: "number", minimum: 40, maximum: 2000, default: 640 } } } },
    { name: "browser_screenshot", description: "获取当前页面截图。", inputSchema: { type: "object", additionalProperties: false, properties: { fullPage: { type: "boolean", default: false } } } },
    { name: "browser_upload", description: "将本地文件上传到最近一次 browser_snapshot 返回的文件输入元素。需要在设置中开启上传权限。", inputSchema: { type: "object", additionalProperties: false, required: ["ref", "path"], properties: { ref: { type: "string", pattern: "^e[0-9]+$" }, path: { type: "string", minLength: 1 } } } },
    { name: "browser_download", description: "点击最近一次快照中的下载元素并保存文件。需要在设置中开启下载权限。", inputSchema: { type: "object", additionalProperties: false, required: ["ref"], properties: { ref: { type: "string", pattern: "^e[0-9]+$" }, path: { type: "string", description: "目标文件路径；省略时保存到用户 Downloads 目录。" } } } },
    { name: "browser_evaluate", description: "在当前页面执行任意 JavaScript 表达式。需要在设置中显式开启高风险权限。", inputSchema: { type: "object", additionalProperties: false, required: ["expression"], properties: { expression: { type: "string", minLength: 1, maxLength: 20000 } } } },
    { name: "browser_mouse", description: "执行坐标级鼠标移动、点击、双击、按下或抬起。需要在设置中显式开启高风险权限。", inputSchema: { type: "object", additionalProperties: false, required: ["x", "y"], properties: { action: { type: "string", enum: ["move", "click", "dblclick", "down", "up"], default: "click" }, x: { type: "number", minimum: 0 }, y: { type: "number", minimum: 0 }, button: { type: "string", enum: ["left", "right", "middle"], default: "left" } } } },
    { name: "browser_keyboard", description: "执行坐标级键盘按键、输入或插入文本。需要在设置中显式开启高风险权限。", inputSchema: { type: "object", additionalProperties: false, properties: { action: { type: "string", enum: ["press", "type", "insertText"], default: "press" }, key: { type: "string" }, text: { type: "string", maxLength: 20000 } } } },
  ];
}

function result(value: unknown): { content: Content[]; structuredContent: unknown } {
  return { content: [{ type: "text", text: JSON.stringify(value) }], structuredContent: value };
}

function responseError(response: BrowserRpcResponse): BrowserMcpError {
  return new BrowserMcpError(response.error?.code ?? "BROWSER_ERROR", response.error?.message ?? "BrowserHost 请求失败。" );
}

function send(message: unknown): void { process.stdout.write(`${JSON.stringify(message)}\n`); }
function argument(name: string): string | undefined { const index = process.argv.indexOf(name); return index >= 0 ? process.argv[index + 1] : process.argv.find((value) => value.startsWith(`${name}=`))?.slice(name.length + 1); }
function isRecord(value: unknown): value is Record<string, unknown> { return Boolean(value) && typeof value === "object" && !Array.isArray(value); }
function isDirection(value: unknown): value is "up" | "down" | "left" | "right" { return value === "up" || value === "down" || value === "left" || value === "right"; }
function isPermission(value: unknown): value is BrowserPermissionName { return value === "navigate" || value === "click" || value === "fill" || value === "press" || value === "scroll" || value === "screenshot" || value === "upload" || value === "download" || value === "evaluate" || value === "coordinate"; }
function isMouseButton(value: unknown): value is "left" | "right" | "middle" { return value === "left" || value === "right" || value === "middle"; }
function finiteCoordinate(value: unknown): number { const number = typeof value === "number" ? value : Number(value); if (!Number.isFinite(number) || number < 0 || number > 100_000) throw new BrowserMcpError("BROWSER_INVALID_COORDINATE", "坐标必须是 0 到 100000 之间的有限数字。"); return number; }
function safeFilename(value: string): string { const filename = value.replace(/[<>:"/\\|?*\u0000-\u001F]/g, "_").trim(); return filename || "download"; }
function jsonSafe(value: unknown): unknown { try { return JSON.parse(JSON.stringify(value, (_key, item) => typeof item === "bigint" ? `${item}n` : item)); } catch { return String(value); } }
function errorCode(error: unknown): string { return typeof (error as any)?.code === "string" ? (error as any).code : "BROWSER_MCP_ERROR"; }
