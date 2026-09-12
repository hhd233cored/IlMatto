# IlMatto Browser MCP

IlMatto now mounts two independent, session-scoped MCP servers when a unified
Antigravity session starts:

- `ilmatto-agent-tools-*` — local companion-memory tools;
- `ilmatto-browser-*` — the small interactive browser surface.

The Browser MCP is started by Antigravity on demand. The first browser tool
call asks ManagerHost to start one headed Chrome instance with the isolated
profile `%LOCALAPPDATA%\IlMatto\browser-profile`; the window is visible and
maximized from the first launch. It does not use the user's normal Chrome profile and does not
download a Playwright browser. The MCP process connects to the returned CDP
endpoint with the pinned
`playwright-core` dependency.

浏览器生命周期控制集中在 ManagerHost 的 `BrowserController` 中，WPF
仅负责状态展示和显示浏览器；这样 ManagerHost 与 MCP
共享同一个浏览器锁，不会出现两个进程重复管理 Chrome。

主页面的浏览器图标是单一入口：浏览器未启动或启动失败时点击会启动/重试，
浏览器已启动时点击会显示现有窗口，不会创建第二个实例。用户手动关闭最后
一个浏览器窗口后，ManagerHost 会把状态更新为 `Stopped`；应用正常退出时则
由关闭流程负责清理，不会把预期的进程结束误报为错误。

## Tools

The Browser MCP exposes the following tools:

`browser_tabs`, `browser_navigate`, `browser_snapshot`, `browser_click`,
`browser_fill`, `browser_press`, `browser_scroll`, `browser_screenshot`,
`browser_upload`, `browser_download`, `browser_evaluate`, `browser_mouse`,
and `browser_keyboard`.

Element actions use refs from the most recent `browser_snapshot`, and
navigation is limited to `http`/`https` URLs. Upload and download accept local
paths, `browser_evaluate` runs a page JavaScript expression, and the mouse and
keyboard tools provide coordinate-level input. These four higher-risk
capabilities are disabled by default and must be enabled in the desktop
settings. Navigation, clicking, filling, pressing, scrolling, and screenshots
are enabled by default and can also be switched off there.

An enabled operation no longer opens a per-action approval dialog. The
desktop keeps the dedicated browser's lifecycle and human-verification state.
If a page looks like a CAPTCHA, login confirmation, 2FA, or other
human-verification page, writes are paused, and the browser is shown. The user
can complete the challenge directly in the browser; no desktop confirmation is
required. The next read-only snapshot checks that the challenge has gone away
and automatically resumes agent writes. Browser extensions are not exposed.

The MCP transport is disposable: if Antigravity restarts the Browser MCP
process, it only detaches the named-pipe connection. It does not call
Playwright `browser.close()` or stop Chrome; the ManagerHost remains the sole
owner of the browser process. Health checks only probe the CDP endpoint and do
not show, hide, resize, or terminate the headed window; visibility changes are
performed only by the desktop browser button or lifecycle cleanup.

## Configuration

The default executable search covers installed Chrome and Edge. Set
`ILMATTO_BROWSER_CHROME_PATH` to an explicit executable when required. Set
`ILMATTO_BROWSER_PROFILE` to override the managed profile location. Both are
optional and are process-local settings; no user Chrome profile is modified.
