# IlMatto 打包与发布指南

本文说明当前版本在 Windows 上的构建、发布和分发方式。发布目标是一个包含 WPF 桌面端和两个 Node.js Host 的目录，而不是只复制一个 `.exe`。

## 1. 发布物和运行时边界

| 部件 | 发布方式 | 目标机器要求 |
| --- | --- | --- |
| `IlMatto.Desktop` | `dotnet publish` 生成 WPF 文件 | .NET 8 Desktop Runtime（框架依赖发布）或随包携带的 .NET 运行时（自包含发布） |
| `ManagerHost` | TypeScript 编译后的 `dist/`，以及生产依赖 | Node.js 22+ |
| `AgentHost` | TypeScript 编译后的 `dist/`，以及生产依赖 | Node.js 22+；仅打开 Pi 工作台时使用 |
| Antigravity CLI | 不随 IlMatto 打包 | 安装 `agy`，并在可见终端完成一次登录 |
| Browser MCP | 随 `ManagerHost` 脚本和 `playwright-core` 发布 | 已安装的 Chrome 或 Edge；不会下载 Chromium |

当前桌面端通过 `ProcessStartInfo.FileName = "node"` 启动 Host，因此暂时不能只分发一个完全脱离 Node.js 的 EXE。若要做到真正单文件发布，需要另行增加内置 Node 运行时或 Node 路径配置，这不属于当前打包流程。

统一 Manager 使用 Antigravity CLI 文本通道，当前源码中没有需要随发布物携带的 Python Bridge 项目。Python 只适用于旧版本兼容代码，不是当前 Manager 的必需项。

## 2. 构建前检查

在 PowerShell 中确认版本：

```powershell
dotnet --info
node --version       # 应为 22 或更高版本
npm --version
agy --version
```

发布机器还需要：

- Windows 10/11（建议 x64）。
- .NET 8 SDK；如果使用框架依赖发布，目标机需要 .NET 8 Desktop Runtime。
- Node.js 22+ 和 npm，并且 `node` 在 PATH 中。
- Antigravity CLI `agy`。默认设置路径为 `%LocalAppData%\agy\bin\agy.exe`，也可以在 IlMatto 设置中改为其他路径。
- 使用 Browser MCP 时，需要已安装 Chrome 或 Edge。浏览器会使用独立的 `%LocalAppData%\IlMatto\browser-profile`，不会连接日常 Chrome Profile。
- 使用 Pi 工作台的 Git、搜索或命令工具时，目标机应准备好 `git`、`rg` 和 PowerShell。

不要把用户的 API Key、`%USERPROFILE%\.gemini` 配置、会话目录或浏览器 Profile 放入安装包。

## 3. 编译两个 Node Host

`dist/` 和 `node_modules/` 都是生成目录，默认被 `.gitignore` 忽略。每次发布都应按照锁文件重新安装并编译：

```powershell
Set-Location "C:\Users\33612\Documents\GitHub\IlMatto"

npm.cmd ci --prefix .\src\IlMatto.ManagerHost
npm.cmd run build --prefix .\src\IlMatto.ManagerHost

npm.cmd ci --prefix .\src\IlMatto.AgentHost
npm.cmd run build --prefix .\src\IlMatto.AgentHost
```

`npm ci` 会严格使用各 Host 的 `package-lock.json`。不要在发布前用未锁版本的 `npm install` 替换它。

可先运行 Host 测试：

```powershell
npm.cmd test --prefix .\src\IlMatto.ManagerHost
npm.cmd test --prefix .\src\IlMatto.AgentHost
```

桌面端回归检查使用独立的临时数据，不会连接 Host 或读取用户凭据：

```powershell
dotnet run --project .\tests\IlMatto.Desktop.Tests\IlMatto.Desktop.Tests.csproj -c Release
```

## 4. 发布 WPF 桌面端

推荐先使用框架依赖发布，体积更小，也与当前运行方式一致：

```powershell
$repo = "C:\Users\33612\Documents\GitHub\IlMatto"
$publish = Join-Path $repo "artifacts\publish-win-x64"
Set-Location $repo

dotnet restore .\IlMatto.sln
dotnet publish .\src\IlMatto.Desktop\IlMatto.Desktop.csproj `
  -c Release `
  -r win-x64 `
  --self-contained false `
  --no-restore `
  -p:PublishSingleFile=false `
  -p:PublishReadyToRun=false `
  -o $publish
```

`IlMatto.Desktop.csproj` 会自动把已经存在的两个 Host 的 `dist/` 和 `package.json` 复制到发布目录的 `ManagerHost\`、`AgentHost\` 子目录。发布命令不会复制 Node 的 `node_modules`，下一节必须补齐它们。

如果目标机不准备安装 .NET 8 Desktop Runtime，可以改用自包含发布：

```powershell
dotnet publish .\src\IlMatto.Desktop\IlMatto.Desktop.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=false -o $publish
```

自包含发布只解决 .NET 运行时依赖，仍然需要 Node.js 22+ 和 `agy`；它的体积也会明显增加。

## 5. 把 Node 生产依赖放入发布目录

发布目录应使用生产依赖，不要把 TypeScript 等开发依赖带给最终用户。先复制锁文件，再在发布目录安装：

```powershell
$hostDefinitions = @(
  @{ Name = "ManagerHost"; Source = (Join-Path $repo "src\IlMatto.ManagerHost") },
  @{ Name = "AgentHost";   Source = (Join-Path $repo "src\IlMatto.AgentHost") }
)

foreach ($item in $hostDefinitions) {
  $target = Join-Path $publish $item.Name
  Copy-Item (Join-Path $item.Source "package-lock.json") `
            (Join-Path $target "package-lock.json") -Force
  npm.cmd ci --omit=dev --prefix $target
}
```

如果发布机不能访问 npm registry，可以把已经安装好的 `node_modules` 复制到对应目录，再执行 `npm.cmd prune --omit=dev --prefix <目录>` 清理开发依赖。离线复制必须来自同一份锁文件和同一套 Node 版本。

最终目录至少应类似于：

```text
publish-win-x64/
├── IlMatto.Desktop.exe
├── IlMatto.Desktop.dll
├── *.dll / *.json
├── ManagerHost/
│   ├── package.json
│   ├── package-lock.json
│   ├── dist/index.js
│   └── node_modules/playwright-core/
└── AgentHost/
    ├── package.json
    ├── package-lock.json
    ├── dist/index.js
    └── node_modules/@mariozechner/
```

不要移动或重命名 `ManagerHost`、`AgentHost`、`dist`。桌面端会优先从 EXE 同级目录查找这两个脚本；找不到时才尝试开发环境的源码回退路径。

## 6. 发布前冒烟验证

在不打开源码目录中的旧实例的情况下，从发布目录启动：

```powershell
$exe = Join-Path $publish "IlMatto.Desktop.exe"
Test-Path (Join-Path $publish "ManagerHost\dist\index.js")
Test-Path (Join-Path $publish "AgentHost\dist\index.js")
Test-Path (Join-Path $publish "ManagerHost\node_modules\playwright-core")
& $exe
```

建议至少检查：

1. 应用启动时没有“找不到 Manager Host”。
2. 发送一条 Manager 消息，确认 Antigravity CLI 能启动并返回文本。
3. 打开 Pi Coding 工作台，确认 AgentHost 能启动；如果不使用 Pi，仍建议保留其目录以避免功能缺失。
4. 点击浏览器按钮，确认 Chrome/Edge 能打开、默认使用新标签页和独立 Profile；Browser MCP 不会在应用启动时预先下载或启动浏览器。
5. 关闭应用后重新启动，确认设置、会话和浏览器状态没有被安装包覆盖。

运行日志位置：

- 启动异常：`%LocalAppData%\IlMatto\startup.log`
- Manager 日志：`%LocalAppData%\IlMatto\logs\`
- 会话和记忆：`%LocalAppData%\IlMatto\manager-sessions\`、`%LocalAppData%\IlMatto\companion-memory\`

## 7. ZIP 或安装包分发

最简单可靠的方式是把整个 `publish-win-x64` 目录压缩为 ZIP。解压后直接运行 `IlMatto.Desktop.exe`，不要只提取 EXE。

```powershell
$zip = Join-Path (Split-Path $publish -Parent) "IlMatto-win-x64.zip"
Compress-Archive -Path (Join-Path $publish "*") -DestinationPath $zip -CompressionLevel Optimal -Force
```

压缩前确认 `ManagerHost\node_modules` 和 `AgentHost\node_modules` 已经生成，并且不要把用于 npm 的临时缓存目录放入 ZIP。

如果使用 Inno Setup、MSIX 或其他安装器：

- 把发布目录的全部文件作为应用内容安装，尤其是两个 Host 目录及其 `node_modules`。
- 安装器应检测 .NET Desktop Runtime、Node.js 22+ 和 `agy`，并在缺失时提示用户。
- 不要在安装阶段创建或覆盖 `%LocalAppData%\IlMatto`、`%USERPROFILE%\.gemini` 或 Windows Credential Manager 中的用户数据。
- 卸载时不要默认删除会话、记忆、凭据和 `browser-profile`；如要提供清理选项，应单独让用户确认。

## 8. 体积和清理建议

源码仓库中的以下目录是可重新生成的构建产物：

```text
**/bin/       **/obj/       **/dist/       **/node_modules/
artifacts/    .vs/         TestResults/   coverage/
```

它们可以在确认没有进程占用后清理，再按本文步骤重建。不要把下面的运行数据当作构建缓存删除：

```text
%LocalAppData%\IlMatto\settings.json
%LocalAppData%\IlMatto\manager-sessions\
%LocalAppData%\IlMatto\companion-memory\
%LocalAppData%\IlMatto\browser-profile\
%USERPROFILE%\.gemini\config\mcp_config.json
```

发布目录中可能出现依赖库的语言资源子目录。不要未经验证批量删除；它们通常是卫星资源程序集，删除后可能只在特定错误或系统语言下暴露问题。更稳妥的做法是先完成冒烟测试，再根据实际保留的语言设置限制卫星资源。

## 9. 常见问题

### `找不到 Manager Host` 或 `找不到 Agent Host`

检查对应的 `dist/index.js` 是否存在，并确认目录名为 `ManagerHost` 或 `AgentHost`。重新执行第 3 节的 `npm.cmd run build`，再重新发布。

### `Cannot find package ... imported from ...`

这是发布目录缺少生产依赖。确认已经复制 `package-lock.json`，并在目标 Host 目录运行：

```powershell
npm.cmd ci --omit=dev --prefix .\ManagerHost
npm.cmd ci --omit=dev --prefix .\AgentHost
```

### `npm ci` 报 `EPERM` 或 npm 缓存目录被占用

Windows 上的杀毒软件或另一个 npm 进程可能暂时锁定用户级 npm 缓存。把缓存切换到发布目录下的临时路径即可：

```powershell
$cache = Join-Path $publish "npm-cache"
New-Item -ItemType Directory -Force -Path $cache | Out-Null
npm.cmd --cache $cache ci --omit=dev --no-audit --no-fund --prefix (Join-Path $publish "ManagerHost")
npm.cmd --cache $cache ci --omit=dev --no-audit --no-fund --prefix (Join-Path $publish "AgentHost")
```

安装完成后，发布包中不需要保留 `$cache` 目录；删除前确认两个 `node_modules` 已完整生成。

### WPF 构建报 `_MarkupCompile.cache` Access denied

先关闭 IlMatto、Visual Studio 和正在使用该项目的调试进程，确认没有文件锁后，只删除当前项目的 `src\IlMatto.Desktop\obj`/`bin`，再重新执行发布。不要删除整个工作区或用户目录。

### `agy` 启动失败或找不到模型

在可见 PowerShell 中运行 `agy --version`、`agy models` 并完成登录；然后在 IlMatto 设置中确认 Antigravity CLI 路径。`agy` 的登录状态不包含在安装包中。

### Browser MCP 启动失败

确认 Chrome 或 Edge 已安装，或设置 `ILMATTO_BROWSER_CHROME_PATH` 指向可执行文件。Browser MCP 只使用固定版本的 `playwright-core` 作为 CDP 客户端，不会下载浏览器内核；不要把日常 Chrome Profile 作为发布目录的一部分。
