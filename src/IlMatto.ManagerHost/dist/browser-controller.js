import { spawn, execFile } from "node:child_process";
import { createServer } from "node:net";
import os from "node:os";
import path from "node:path";
import { existsSync } from "node:fs";
import { mkdir } from "node:fs/promises";
import { defaultBrowserPermissions } from "./protocol.js";
export class BrowserControllerError extends Error {
    code;
    constructor(code, message) {
        super(message);
        this.code = code;
        this.name = "BrowserControllerError";
    }
}
/**
 * Owns one headed, isolated Chrome process for all Manager sessions.  The
 * controller deliberately knows nothing about DOM operations; Browser MCP
 * connects to the returned CDP endpoint with its pinned Playwright client.
 */
export class BrowserController {
    notify;
    state = "Stopped";
    ownerSessionId;
    profilePath;
    cdpEndpoint;
    chrome;
    port;
    visible = false;
    startPromise;
    sessionPermissions = new Map();
    constructor(notify) {
        this.notify = notify;
    }
    get currentState() { return this.state; }
    configureSession(sessionId, permissions) {
        this.sessionPermissions.set(sessionId, { ...defaultBrowserPermissions, ...(permissions ?? {}) });
    }
    async handle(sessionId, request) {
        switch (request.operation) {
            case "start": return await this.start(sessionId);
            case "stop": return await this.stop(sessionId);
            case "state":
                await this.refreshHealth(sessionId);
                return this.snapshotState(sessionId);
            case "authorize": return await this.authorize(sessionId, request);
            case "page_state": return this.pageState(sessionId, request.page);
            default:
                this.ensureOwner(sessionId);
                if (this.state === "Stopped")
                    await this.start(sessionId);
                if ((this.state === "WaitingForHuman" || this.state === "HumanControlled") && isBrowserWriteOperation(request.operation)) {
                    throw new BrowserControllerError("BROWSER_CONTROLLED_BY_HUMAN", this.state === "WaitingForHuman"
                        ? "浏览器正在等待人工验证，请直接在浏览器中完成验证；Agent 会在下一次页面快照后自动恢复。"
                        : "浏览器当前由人工控制，请先结束人工接管。");
                }
                return this.snapshotState(sessionId);
        }
    }
    async setVisibility(sessionId, visible) {
        this.ensureOwner(sessionId);
        // The desktop's "打开浏览器" action is also the recovery path after a
        // crashed/closed browser. Retry startup from Error instead of merely
        // attempting ShowWindow on a process that has already exited.
        const starting = this.state === "Stopped" || this.state === "Error";
        if (starting)
            await this.start(sessionId);
        // startCore already maximizes a new browser. Calling SW_RESTORE here in
        // the same request can immediately undo that choice by restoring the
        // profile's previous small bounds, so only use restore for an existing
        // browser and keep the first visible request maximized.
        await this.showWindow(visible, visible && starting);
        this.visible = visible;
        // Visibility is not ownership. Showing the headed browser must not put
        // it into HumanControlled mode: users may inspect or interact with it
        // while the agent is idle, and the agent can continue using the same
        // singleton instance when a turn resumes.
        this.emitState(sessionId);
        return this.snapshotState(sessionId);
    }
    async humanDone(sessionId) {
        this.ensureOwner(sessionId);
        if (this.state !== "WaitingForHuman" && this.state !== "HumanControlled")
            return this.snapshotState(sessionId);
        this.state = "AgentControlled";
        // Completing verification returns control to the agent, but must not hide
        // the headed browser.  Hiding here made it look as if Chrome had crashed
        // while the CDP connection remained usable by the agent.
        this.visible = true;
        await this.showWindow(true);
        this.emitState(sessionId, "人工验证已完成，下一次操作前会重新获取页面状态。");
        return this.snapshotState(sessionId);
    }
    approve(sessionId, actionId, approved) {
        // Kept as a protocol-compatible no-op for older desktop clients. Browser
        // permissions are controlled by the settings list instead of a per-action
        // approval dialog.
    }
    async dispose() {
        await this.stopProcess();
        this.state = "Stopped";
        this.ownerSessionId = undefined;
        this.cdpEndpoint = undefined;
        this.profilePath = undefined;
        this.port = undefined;
        this.sessionPermissions.clear();
    }
    async release(sessionId) {
        this.sessionPermissions.delete(sessionId);
        if (this.ownerSessionId !== sessionId)
            return;
        await this.stopProcess();
        this.state = "Stopped";
        this.ownerSessionId = undefined;
        this.cdpEndpoint = undefined;
        this.port = undefined;
        this.visible = false;
    }
    async start(sessionId) {
        if (this.ownerSessionId && this.ownerSessionId !== sessionId) {
            throw new BrowserControllerError("BROWSER_LOCKED", "浏览器正在由另一个 Manager 会话使用。");
        }
        this.ownerSessionId = sessionId;
        if (this.cdpEndpoint && this.chrome && !this.chrome.killed && this.chrome.exitCode === null) {
            return this.snapshotState(sessionId);
        }
        // A failed CDP probe can leave the launcher process around for a short
        // time. Reap it before reusing the profile, otherwise the next launch can
        // race the old process and inherit a stale remote-debugging state.
        if (this.chrome)
            await this.stopProcess();
        if (this.startPromise)
            return await this.startPromise;
        this.startPromise = this.startCore(sessionId);
        try {
            return await this.startPromise;
        }
        finally {
            this.startPromise = undefined;
        }
    }
    async startCore(sessionId) {
        this.state = "Starting";
        this.emitState(sessionId, "正在启动专用浏览器…");
        const executable = resolveChromeExecutable();
        if (!executable) {
            this.state = "Error";
            this.emitState(sessionId, "找不到 Chrome 或 Edge 浏览器可执行文件。");
            throw new BrowserControllerError("BROWSER_CHROME_NOT_FOUND", "找不到 Chrome/Edge。请安装浏览器，或设置 ILMATTO_BROWSER_CHROME_PATH。");
        }
        const localAppData = process.env.LOCALAPPDATA ?? path.join(os.homedir(), "AppData", "Local");
        this.profilePath = path.resolve(process.env.ILMATTO_BROWSER_PROFILE ?? path.join(localAppData, "IlMatto", "browser-profile"));
        await mkdir(this.profilePath, { recursive: true });
        this.port = await findFreePort();
        const newTabUrl = resolveBrowserNewTabUrl(executable);
        const args = [
            `--remote-debugging-port=${this.port}`,
            "--remote-debugging-address=127.0.0.1",
            "--remote-allow-origins=*",
            `--user-data-dir=${this.profilePath}`,
            "--no-first-run",
            "--no-default-browser-check",
            "--disable-features=Translate,OptimizationHints",
            "--start-maximized",
            "--new-window",
            newTabUrl,
        ];
        // Keep the headed browser visible from its first launch.  The desktop can
        // still explicitly hide it later through browser_set_visibility.
        const chrome = spawn(executable, args, { windowsHide: false, stdio: "ignore" });
        this.chrome = chrome;
        chrome.once("exit", (code, signal) => {
            // Ignore an old launcher's exit event after a recovery launch has
            // replaced it. Without this identity check a stale event can clear the
            // new CDP endpoint and make a healthy browser appear closed.
            if (this.chrome !== chrome)
                return;
            this.chrome = undefined;
            this.cdpEndpoint = undefined;
            this.port = undefined;
            this.visible = false;
            if (this.state === "Stopped")
                return;
            // A normal exit is what Chromium reports when the user closes the last
            // browser window. Treat it as an intentional stop so the desktop can
            // immediately return the button to its idle state. Keep unexpected
            // exits as Error so the same button remains a visible recovery path.
            const normalExit = code === 0 && signal === null;
            this.ownerSessionId = undefined;
            this.state = normalExit ? "Stopped" : "Error";
            this.emitState(sessionId, normalExit
                ? "浏览器窗口已关闭，点击浏览器图标可重新启动。"
                : "浏览器进程意外退出，点击浏览器图标可重试。");
        });
        try {
            const endpoint = await Promise.race([
                waitForCdp(this.port, chrome),
                new Promise((_resolve, reject) => chrome.once("error", reject)),
            ]);
            this.cdpEndpoint = endpoint;
            this.state = "AgentControlled";
            this.visible = true;
            await this.showWindow(true, true);
            this.emitState(sessionId);
            return this.snapshotState(sessionId);
        }
        catch (error) {
            await this.stopProcess();
            this.state = "Error";
            const message = error instanceof Error ? error.message : "CDP 连接失败。";
            this.emitState(sessionId, message);
            throw new BrowserControllerError("BROWSER_CDP_CONNECT_FAILED", message);
        }
    }
    async stop(sessionId) {
        this.ensureOwner(sessionId);
        await this.stopProcess();
        this.state = "Stopped";
        this.ownerSessionId = undefined;
        this.cdpEndpoint = undefined;
        this.port = undefined;
        this.visible = false;
        this.emitState(sessionId);
        return this.snapshotState(sessionId);
    }
    async stopProcess() {
        const child = this.chrome;
        this.chrome = undefined;
        if (!child || child.killed)
            return;
        if (process.platform === "win32" && child.pid) {
            await new Promise((resolve) => {
                execFile("taskkill.exe", ["/PID", String(child.pid), "/T", "/F"], { windowsHide: true }, () => resolve());
            });
        }
        else {
            try {
                child.kill("SIGTERM");
            }
            catch { /* process already exited */ }
        }
    }
    async authorize(sessionId, request) {
        this.ensureOwner(sessionId);
        await this.refreshHealth(sessionId);
        if (request.write !== false && (this.state === "WaitingForHuman" || this.state === "HumanControlled")) {
            throw new BrowserControllerError("BROWSER_CONTROLLED_BY_HUMAN", this.state === "WaitingForHuman"
                ? "浏览器正在等待人工验证，请直接在浏览器中完成验证；Agent 会在下一次页面快照后自动恢复。"
                : "浏览器当前由人工控制，写操作已暂停。");
        }
        const permission = request.permission ?? permissionFromDetails(request.details);
        const permissions = this.sessionPermissions.get(sessionId) ?? defaultBrowserPermissions;
        if (!permission || !permissions[permission]) {
            throw new BrowserControllerError("BROWSER_PERMISSION_DENIED", `浏览器权限“${permission ?? "unknown"}”未开启，请在设置中允许后重试。`);
        }
        // Permissions are explicit settings. An enabled operation is allowed
        // immediately; a disabled operation is rejected above.
        return { state: this.state, profilePath: this.profilePath, cdpEndpoint: this.cdpEndpoint, visible: this.visible, data: { permission, allowed: true } };
    }
    pageState(sessionId, page) {
        this.ensureOwner(sessionId);
        const url = page?.url ?? "";
        const title = page?.title ?? "";
        const text = page?.text ?? "";
        const risk = `${url}\n${title}\n${text}`;
        if (looksLikeHumanVerification(risk)) {
            this.state = "WaitingForHuman";
            this.visible = true;
            void this.showWindow(true);
            this.emitState(sessionId, "检测到登录、验证码、2FA 或人工验证页面。请直接在浏览器中完成验证；下一次页面快照会自动恢复 Agent。");
        }
        else if (this.state === "WaitingForHuman" && (url || title || text)) {
            // A read-only snapshot after the user finishes the challenge is enough
            // to resume. No extra desktop acknowledgement is required.
            this.state = "AgentControlled";
            this.visible = true;
            this.emitState(sessionId, "验证页面已离开，已自动恢复 Agent 控制。");
        }
        return this.snapshotState(sessionId);
    }
    ensureOwner(sessionId) {
        if (this.ownerSessionId && this.ownerSessionId !== sessionId) {
            throw new BrowserControllerError("BROWSER_LOCKED", "浏览器正在由另一个 Manager 会话使用。");
        }
        if (!this.ownerSessionId)
            this.ownerSessionId = sessionId;
    }
    snapshotState(sessionId) {
        return { state: this.state, profilePath: this.profilePath, cdpEndpoint: this.cdpEndpoint, visible: this.visible, data: { ownerSessionId: this.ownerSessionId ?? sessionId } };
    }
    /**
     * Check the CDP endpoint without touching the headed window. Browser MCP
     * asks for this state before every operation; any ShowWindow call here can
     * unexpectedly change a user's maximized/minimized state, and killing the
     * process on one transient probe failure can close a healthy browser. The
     * child-process exit handler remains responsible for normal manual-close
     * detection; a later operation can restart a confirmed broken endpoint.
     */
    async refreshHealth(sessionId) {
        if (this.state === "Stopped" || this.state === "Starting")
            return;
        const child = this.chrome;
        if (!child || child.killed || child.exitCode !== null || !this.port || !this.cdpEndpoint)
            return;
        let healthy = false;
        let lastError = "浏览器 CDP 探测失败。";
        for (let attempt = 0; attempt < 2; attempt++) {
            try {
                const response = await fetch(`http://127.0.0.1:${this.port}/json/version`, { signal: AbortSignal.timeout(1_500) });
                if (!response.ok)
                    throw new Error(`CDP 返回 HTTP ${response.status}。`);
                healthy = true;
                break;
            }
            catch (error) {
                lastError = error instanceof Error ? error.message : lastError;
                if (attempt === 0)
                    await new Promise((resolve) => setTimeout(resolve, 200));
            }
        }
        if (!healthy) {
            // Do not call stopProcess here. Browser MCP will request a fresh start
            // from the Error state, and the normal exit handler will still publish
            // a clean Stopped state when the user closes the window.
            this.cdpEndpoint = undefined;
            if (this.state !== "Error") {
                this.state = "Error";
                this.emitState(sessionId, `浏览器 CDP 连接已断开（${lastError}）。下一次浏览器操作会尝试重新启动。`);
            }
        }
    }
    emitState(sessionId, message) {
        this.notify({ type: "browser_state", sessionId, state: this.state, ownerSessionId: this.ownerSessionId, profilePath: this.profilePath, cdpEndpoint: this.cdpEndpoint, visible: this.visible, message });
    }
    async showWindow(visible, maximize = false) {
        const pid = this.chrome?.pid;
        if (process.platform !== "win32" || !pid)
            return false;
        // Chrome can hand the visible top-level window to a child process.  Do
        // not rely on the launcher's MainWindowHandle: walk the process tree and
        // apply ShowWindow to every top-level window that belongs to it.  Showing
        // retries briefly because the CDP endpoint can be ready before the first
        // browser window has created its HWND.
        // SW_RESTORE can undo a maximized window by restoring its previous
        // bounds. Use SW_SHOW for ordinary display/health checks so Agent calls
        // never resize a browser the user has already maximized or adjusted.
        const command = visible ? (maximize ? 3 : 5) : 0; // SW_MAXIMIZE / SW_SHOW / SW_HIDE
        const script = `$rootPid = ${Math.trunc(pid)}
$showCommand = ${command}
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class IlMattoWin32 {
  [DllImport("user32.dll")]
  public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
'@
for ($attempt = 0; $attempt -lt 20; $attempt++) {
  $all = @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue)
  $ids = [System.Collections.Generic.HashSet[int]]::new()
  [void]$ids.Add($rootPid)
  $changed = $true
  while ($changed) {
    $changed = $false
    foreach ($item in $all) {
      $childId = [int]$item.ProcessId
      if ($ids.Contains([int]$item.ParentProcessId) -and $ids.Add($childId)) {
        $changed = $true
      }
    }
  }
  $found = $false
  foreach ($id in $ids) {
    $p = Get-Process -Id $id -ErrorAction SilentlyContinue
    if ($p -and $p.MainWindowHandle -ne 0) {
      [IlMattoWin32]::ShowWindow($p.MainWindowHandle, $showCommand) | Out-Null
      $found = $true
    }
  }
  # Chrome may restore the profile's previous bounds immediately after the
  # first HWND appears. Keep applying SW_MAXIMIZE briefly during startup so
  # that the requested default wins that race; normal show/hide operations
  # still finish on their first successful probe.
  if ($showCommand -eq 0 -or ($found -and $showCommand -ne 3) -or ($found -and $attempt -ge 19)) { break }
  Start-Sleep -Milliseconds 100
}
Write-Output ($(if ($found) { "1" } else { "0" }))`;
        return await new Promise((resolve) => execFile("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", script], { windowsHide: true }, (error, stdout) => {
            if (error) {
                resolve(false);
                return;
            }
            resolve(stdout.trim().split(/\r?\n/).at(-1) === "1");
        }));
    }
}
function resolveChromeExecutable() {
    const configured = process.env.ILMATTO_BROWSER_CHROME_PATH?.trim();
    if (configured)
        return existsSync(configured) ? configured : undefined;
    if (process.platform !== "win32")
        return configured || "google-chrome";
    const candidates = [
        path.join(process.env.PROGRAMFILES ?? "", "Google", "Chrome", "Application", "chrome.exe"),
        path.join(process.env["PROGRAMFILES(X86)"] ?? "", "Google", "Chrome", "Application", "chrome.exe"),
        path.join(process.env.LOCALAPPDATA ?? "", "Google", "Chrome", "Application", "chrome.exe"),
        path.join(process.env.PROGRAMFILES ?? "", "Microsoft", "Edge", "Application", "msedge.exe"),
        path.join(process.env["PROGRAMFILES(X86)"] ?? "", "Microsoft", "Edge", "Application", "msedge.exe"),
    ];
    return candidates.find((candidate) => candidate && existsSync(candidate));
}
function resolveBrowserNewTabUrl(executable) {
    return path.basename(executable).toLowerCase() === "msedge.exe" ? "edge://newtab/" : "chrome://newtab/";
}
async function findFreePort() {
    return await new Promise((resolve, reject) => {
        const server = createServer();
        server.once("error", reject);
        server.listen(0, "127.0.0.1", () => {
            const address = server.address();
            const port = address && typeof address === "object" ? address.port : 0;
            server.close((error) => error ? reject(error) : resolve(port));
        });
    });
}
async function waitForCdp(port, child) {
    const deadline = Date.now() + 20_000;
    let lastError = "CDP endpoint 尚未就绪。";
    while (Date.now() < deadline) {
        if (child.exitCode !== null)
            throw new Error(`Chrome 在 CDP 启动前退出（退出码 ${child.exitCode ?? "unknown"}）。`);
        try {
            const response = await fetch(`http://127.0.0.1:${port}/json/version`);
            if (response.ok) {
                const payload = await response.json();
                if (typeof payload.webSocketDebuggerUrl === "string" && payload.webSocketDebuggerUrl)
                    return payload.webSocketDebuggerUrl;
                return `http://127.0.0.1:${port}`;
            }
            lastError = `CDP 返回 HTTP ${response.status}。`;
        }
        catch (error) {
            lastError = error instanceof Error ? error.message : lastError;
        }
        await new Promise((resolve) => setTimeout(resolve, 150));
    }
    throw new Error(`连接 Chrome CDP 超时：${lastError}`);
}
function looksLikeHumanVerification(value) {
    return /(captcha|recaptcha|hcaptcha|two[- ]?factor|2fa|one[- ]time password|verify you are human|human verification|验证码|人机验证|安全验证|登录确认|二次验证|两步验证)/i.test(value);
}
export function isBrowserWriteOperation(operation) {
    return operation === "navigate" || operation === "click" || operation === "fill" || operation === "press" || operation === "upload" || operation === "download" || operation === "evaluate" || operation === "mouse" || operation === "keyboard";
}
function permissionFromDetails(details) {
    const tool = details?.split("\n", 1)[0]?.trim();
    return tool === "browser_navigate" ? "navigate" :
        tool === "browser_click" ? "click" :
            tool === "browser_fill" ? "fill" :
                tool === "browser_press" ? "press" :
                    tool === "browser_scroll" ? "scroll" :
                        tool === "browser_screenshot" ? "screenshot" :
                            tool === "browser_upload" ? "upload" :
                                tool === "browser_download" ? "download" :
                                    tool === "browser_evaluate" ? "evaluate" :
                                        tool === "browser_mouse" || tool === "browser_keyboard" ? "coordinate" : undefined;
}
//# sourceMappingURL=browser-controller.js.map