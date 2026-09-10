import assert from "node:assert/strict";
import { test } from "node:test";
import os from "node:os";
import path from "node:path";
import { mkdir, mkdtemp, readFile, rm, stat, writeFile } from "node:fs/promises";
import { buildCompanionWebPermissionRules, buildManagerPermissionRules, cleanupManagerRuntime, cleanupManagerSessionData, ensureManagerRuntime, ensureUnifiedManagerRuntime, managerActionSchema } from "./runtime.js";

test("unified manager runtime uses the selected workspace without generating an AGY agent", async () => {
  const workspace = await mkdtemp(path.join(os.tmpdir(), "ilmatto-unified-workspace-"));
  const runtimeRoot = await mkdtemp(path.join(os.tmpdir(), "ilmatto-unified-runtime-"));
  const previousRuntime = process.env.ILMATTO_MANAGER_RUNTIME;
  const previousLog = process.env.ILMATTO_MANAGER_LOG_DIR;
  const previousAgentDir = process.env.ILMATTO_MANAGER_AGENT_DIR;
  process.env.ILMATTO_MANAGER_RUNTIME = runtimeRoot;
  process.env.ILMATTO_MANAGER_LOG_DIR = path.join(runtimeRoot, "logs");
  process.env.ILMATTO_MANAGER_AGENT_DIR = path.join(runtimeRoot, "global-agents");
  try {
    const staleAgent = path.join(runtimeRoot, ".agents", "agents", "ilmatto-manager");
    await mkdir(staleAgent, { recursive: true });
    await writeFile(path.join(staleAgent, "agent.md"), "legacy", "utf8");
    await writeFile(path.join(runtimeRoot, "manager-action.schema.json"), "{}", "utf8");
    const runtime = await ensureUnifiedManagerRuntime(workspace);
    assert.equal(runtime.root, path.resolve(workspace));
    assert.equal(runtime.agentName, "");
    assert.equal(runtime.agentPath, "");
    assert.equal(runtime.schemaPath, "");
    await assert.doesNotReject(stat(runtime.attachmentsRoot));
    await assert.rejects(stat(path.join(workspace, ".agents")));
    await assert.rejects(stat(path.join(runtimeRoot, "manager-action.schema.json")));
    await assert.rejects(stat(staleAgent));
    await cleanupManagerRuntime(runtime);
    await assert.doesNotReject(stat(workspace));
  } finally {
    if (previousRuntime === undefined) delete process.env.ILMATTO_MANAGER_RUNTIME; else process.env.ILMATTO_MANAGER_RUNTIME = previousRuntime;
    if (previousLog === undefined) delete process.env.ILMATTO_MANAGER_LOG_DIR; else process.env.ILMATTO_MANAGER_LOG_DIR = previousLog;
    if (previousAgentDir === undefined) delete process.env.ILMATTO_MANAGER_AGENT_DIR; else process.env.ILMATTO_MANAGER_AGENT_DIR = previousAgentDir;
    await rm(workspace, { recursive: true, force: true });
    await rm(runtimeRoot, { recursive: true, force: true });
  }
});

test("session cleanup removes owned memory and attachment copies but not the shared profile", async () => {
  const workspace = await mkdtemp(path.join(os.tmpdir(), "ilmatto-session-cleanup-workspace-"));
  const runtimeRoot = await mkdtemp(path.join(os.tmpdir(), "ilmatto-session-cleanup-runtime-"));
  const localAppData = await mkdtemp(path.join(os.tmpdir(), "ilmatto-session-cleanup-local-"));
  const memoryRoot = path.join(localAppData, "memory");
  const sessionId = "cleanup-session";
  const previousRuntime = process.env.ILMATTO_MANAGER_RUNTIME;
  const previousLocalAppData = process.env.LOCALAPPDATA;
  const previousMemory = process.env.ILMATTO_COMPANION_MEMORY_DIR;
  process.env.ILMATTO_MANAGER_RUNTIME = runtimeRoot;
  process.env.LOCALAPPDATA = localAppData;
  process.env.ILMATTO_COMPANION_MEMORY_DIR = memoryRoot;
  try {
    const runtime = await ensureUnifiedManagerRuntime(workspace);
    const profilePath = path.join(memoryRoot, "profile.md");
    const memorySession = path.join(memoryRoot, "sessions", sessionId);
    const managedAttachments = path.join(localAppData, "IlMatto", "manager-sessions", "attachments", sessionId);
    const runtimeAttachments = path.join(runtime.attachmentsRoot, sessionId);
    await mkdir(memorySession, { recursive: true });
    await mkdir(managedAttachments, { recursive: true });
    await mkdir(runtimeAttachments, { recursive: true });
    await writeFile(profilePath, "# 用户画像\n", "utf8");
    await writeFile(path.join(memorySession, "summary.json"), "{}", "utf8");
    await writeFile(path.join(managedAttachments, "image.png"), "managed", "utf8");
    await writeFile(path.join(runtimeAttachments, "image.png"), "runtime", "utf8");

    await cleanupManagerSessionData(runtime, sessionId);
    await assert.rejects(stat(memorySession));
    await assert.rejects(stat(managedAttachments));
    await assert.rejects(stat(runtimeAttachments));
    assert.equal(await readFile(profilePath, "utf8"), "# 用户画像\n");
  } finally {
    if (previousRuntime === undefined) delete process.env.ILMATTO_MANAGER_RUNTIME; else process.env.ILMATTO_MANAGER_RUNTIME = previousRuntime;
    if (previousLocalAppData === undefined) delete process.env.LOCALAPPDATA; else process.env.LOCALAPPDATA = previousLocalAppData;
    if (previousMemory === undefined) delete process.env.ILMATTO_COMPANION_MEMORY_DIR; else process.env.ILMATTO_COMPANION_MEMORY_DIR = previousMemory;
    await rm(workspace, { recursive: true, force: true });
    await rm(runtimeRoot, { recursive: true, force: true });
    await rm(localAppData, { recursive: true, force: true });
  }
});

test("unified runtime mounts the Agent Tools MCP in the global Antigravity config", async () => {
  const workspace = await mkdtemp(path.join(os.tmpdir(), "ilmatto-unified-global-mcp-workspace-"));
  const runtimeRoot = await mkdtemp(path.join(os.tmpdir(), "ilmatto-unified-global-mcp-runtime-"));
  const globalConfigPath = path.join(runtimeRoot, ".gemini", "config", "mcp_config.json");
  try {
    await mkdir(path.dirname(globalConfigPath), { recursive: true });
    await writeFile(globalConfigPath, JSON.stringify({ mcpServers: { unityMCP: { serverUrl: "http://127.0.0.1:8080/mcp", type: "http" } } }), "utf8");
    const runtime = await ensureUnifiedManagerRuntime(workspace, undefined, {
      mcp: { command: process.execPath, scriptPath: process.execPath, pipeName: "global-test-pipe", sessionId: "session-global", configPath: globalConfigPath },
    });
    assert.ok(runtime.mcpMount);
    assert.equal(runtime.mcpMount!.scope, "global");
    assert.equal(runtime.mcpMount!.configPath, path.resolve(globalConfigPath));
    assert.match(runtime.mcpMount!.serverName, /^ilmatto-agent-tools-[a-f0-9]{12}$/);
    await assert.rejects(stat(path.join(workspace, ".agents")));
    const mounted = JSON.parse(await readFile(globalConfigPath, "utf8"));
    assert.deepEqual(mounted.mcpServers.unityMCP, { serverUrl: "http://127.0.0.1:8080/mcp", type: "http" });
    assert.deepEqual(mounted.mcpServers[runtime.mcpMount!.serverName].args.slice(-4), ["--pipe", "global-test-pipe", "--session-id", "session-global"]);
    await cleanupManagerRuntime(runtime);
    const cleaned = JSON.parse(await readFile(globalConfigPath, "utf8"));
    assert.deepEqual(cleaned.mcpServers, { unityMCP: { serverUrl: "http://127.0.0.1:8080/mcp", type: "http" } });

    const restarted = await ensureUnifiedManagerRuntime(workspace, undefined, {
      mcp: { command: process.execPath, scriptPath: process.execPath, pipeName: "global-test-restarted", sessionId: "session-global", configPath: globalConfigPath },
    });
    const restartedConfig = JSON.parse(await readFile(globalConfigPath, "utf8"));
    assert.deepEqual(restartedConfig.mcpServers[restarted.mcpMount!.serverName].args.slice(-4), ["--pipe", "global-test-restarted", "--session-id", "session-global"]);
    await cleanupManagerRuntime(restarted);
  } finally {
    await rm(workspace, { recursive: true, force: true });
    await rm(runtimeRoot, { recursive: true, force: true });
  }
});

test("global Agent Tools mount removes only a generated legacy Codex entry", async () => {
  const workspace = await mkdtemp(path.join(os.tmpdir(), "ilmatto-unified-legacy-global-workspace-"));
  const runtimeRoot = await mkdtemp(path.join(os.tmpdir(), "ilmatto-unified-legacy-global-runtime-"));
  const globalConfigPath = path.join(runtimeRoot, "mcp_config.json");
  try {
    await mkdir(path.dirname(globalConfigPath), { recursive: true });
    await writeFile(globalConfigPath, JSON.stringify({ mcpServers: {
      "ilmatto-codex-observation": { command: process.execPath, args: [process.execPath, "--pipe", "old", "--session-id", "old-session"] },
      userLegacy: { command: "user-mcp" },
    } }), "utf8");
    const runtime = await ensureUnifiedManagerRuntime(workspace, undefined, {
      mcp: { command: process.execPath, scriptPath: process.execPath, pipeName: "new", sessionId: "session-1", configPath: globalConfigPath },
    });
    const mounted = JSON.parse(await readFile(globalConfigPath, "utf8"));
    assert.equal(mounted.mcpServers["ilmatto-codex-observation"], undefined);
    assert.deepEqual(mounted.mcpServers.userLegacy, { command: "user-mcp" });
    assert.ok(mounted.mcpServers[runtime.mcpMount!.serverName]);
    await cleanupManagerRuntime(runtime);
  } finally {
    await rm(workspace, { recursive: true, force: true });
    await rm(runtimeRoot, { recursive: true, force: true });
  }
});

test("unified runtime mounts a workspace-local Agent Tools MCP plugin and removes only its own entry", async () => {
  const workspace = await mkdtemp(path.join(os.tmpdir(), "ilmatto-unified-mcp-workspace-"));
  const runtimeRoot = await mkdtemp(path.join(os.tmpdir(), "ilmatto-unified-mcp-runtime-"));
  const previousRuntime = process.env.ILMATTO_MANAGER_RUNTIME;
  const previousLog = process.env.ILMATTO_MANAGER_LOG_DIR;
  try {
    process.env.ILMATTO_MANAGER_RUNTIME = runtimeRoot;
    process.env.ILMATTO_MANAGER_LOG_DIR = path.join(runtimeRoot, "logs");
    await mkdir(path.join(workspace, ".agents", "plugins"), { recursive: true });
    const runtime = await ensureUnifiedManagerRuntime(workspace, undefined, {
      mcp: { command: process.execPath, scriptPath: process.execPath, pipeName: "\\\\.\\pipe\\IlMatto-test", sessionId: "session-1", scope: "workspace-plugin" },
    });
    assert.ok(runtime.mcpMount);
    await writeFile(runtime.mcpMount!.configPath, JSON.stringify({ mcpServers: { userServer: { command: "user-mcp" }, [runtime.mcpMount!.serverName]: runtime.mcpMount!.definition } }), "utf8");
    const mounted = JSON.parse(await readFile(runtime.mcpMount!.configPath, "utf8"));
    const manifest = JSON.parse(await readFile(runtime.mcpMount!.manifestPath!, "utf8"));
    const pluginRegistry = JSON.parse(await readFile(path.join(workspace, ".agents", "plugins.json"), "utf8"));
    assert.equal(manifest.name, runtime.mcpMount!.serverName);
    assert.deepEqual(pluginRegistry.entries, [{ path: ".agents/plugins", include_only: [`^${runtime.mcpMount!.serverName}$`] }]);
    assert.deepEqual(mounted.mcpServers.userServer, { command: "user-mcp" });
    const entry = mounted.mcpServers[runtime.mcpMount!.serverName];
    assert.equal(entry.command, process.execPath);
    assert.deepEqual(entry.args.slice(-4), ["--pipe", "\\\\.\\pipe\\IlMatto-test", "--session-id", "session-1"]);
    await cleanupManagerRuntime(runtime);
    const cleaned = JSON.parse(await readFile(runtime.mcpMount!.configPath, "utf8"));
    assert.deepEqual(cleaned.mcpServers, { userServer: { command: "user-mcp" } });
    await assert.rejects(stat(runtime.mcpMount!.manifestPath!));
    await assert.doesNotReject(stat(runtime.mcpMount!.pluginDirectory!));
    await assert.rejects(stat(path.join(workspace, ".agents", "plugins.json")));
    const restarted = await ensureUnifiedManagerRuntime(workspace, undefined, {
      mcp: { command: process.execPath, scriptPath: process.execPath, pipeName: "\\\\.\\pipe\\IlMatto-test-restarted", sessionId: "session-1", scope: "workspace-plugin" },
    });
    const restartedConfig = JSON.parse(await readFile(restarted.mcpMount!.configPath, "utf8"));
    assert.deepEqual(restartedConfig.mcpServers[restarted.mcpMount!.serverName].args.slice(-4), ["--pipe", "\\\\.\\pipe\\IlMatto-test-restarted", "--session-id", "session-1"]);
    await cleanupManagerRuntime(restarted);
  } finally {
    if (previousRuntime === undefined) delete process.env.ILMATTO_MANAGER_RUNTIME; else process.env.ILMATTO_MANAGER_RUNTIME = previousRuntime;
    if (previousLog === undefined) delete process.env.ILMATTO_MANAGER_LOG_DIR; else process.env.ILMATTO_MANAGER_LOG_DIR = previousLog;
    await rm(workspace, { recursive: true, force: true });
    await rm(runtimeRoot, { recursive: true, force: true });
  }
});

test("global mount removes only stale IlMatto workspace plugins", async () => {
  const workspace = await mkdtemp(path.join(os.tmpdir(), "ilmatto-unified-stale-plugin-workspace-"));
  const runtimeRoot = await mkdtemp(path.join(os.tmpdir(), "ilmatto-unified-stale-plugin-runtime-"));
  const globalConfigPath = path.join(runtimeRoot, "mcp_config.json");
  const staleName = "ilmatto-codex-observation-stale";
  const staleDirectory = path.join(workspace, ".agents", "plugins", staleName);
  try {
    await mkdir(staleDirectory, { recursive: true });
    await writeFile(path.join(staleDirectory, "plugin.json"), JSON.stringify({ name: staleName }), "utf8");
    await writeFile(path.join(staleDirectory, "mcp_config.json"), JSON.stringify({ mcpServers: { [staleName]: { command: process.execPath, args: [process.execPath, "--pipe", "old", "--session-id", "old"] } } }), "utf8");
    await mkdir(path.join(workspace, ".agents"), { recursive: true });
    await writeFile(path.join(workspace, ".agents", "plugins.json"), JSON.stringify({ entries: [{ path: ".agents/plugins", include_only: [`^${staleName}$`] }] }), "utf8");
    const runtime = await ensureUnifiedManagerRuntime(workspace, undefined, {
      mcp: { command: process.execPath, scriptPath: process.execPath, pipeName: "global-pipe", sessionId: "session-global", configPath: globalConfigPath },
    });
    await assert.rejects(stat(staleDirectory));
    await assert.rejects(stat(path.join(workspace, ".agents", "plugins.json")));
    await cleanupManagerRuntime(runtime);
  } finally {
    await rm(workspace, { recursive: true, force: true });
    await rm(runtimeRoot, { recursive: true, force: true });
  }
});

test("unified runtime adds a filtered registration when an existing plugins.json entry excludes IlMatto", async () => {
  const workspace = await mkdtemp(path.join(os.tmpdir(), "ilmatto-unified-mcp-registry-workspace-"));
  const runtimeRoot = await mkdtemp(path.join(os.tmpdir(), "ilmatto-unified-mcp-registry-runtime-"));
  const previousRuntime = process.env.ILMATTO_MANAGER_RUNTIME;
  const previousLog = process.env.ILMATTO_MANAGER_LOG_DIR;
  try {
    process.env.ILMATTO_MANAGER_RUNTIME = runtimeRoot;
    process.env.ILMATTO_MANAGER_LOG_DIR = path.join(runtimeRoot, "logs");
    await mkdir(path.join(workspace, ".agents", "plugins"), { recursive: true });
    const registryPath = path.join(workspace, ".agents", "plugins.json");
    const existing = { path: ".agents/plugins", include_only: ["^user-plugin$"] };
    await writeFile(registryPath, JSON.stringify({ entries: [existing] }), "utf8");
    const runtime = await ensureUnifiedManagerRuntime(workspace, undefined, {
      mcp: { command: process.execPath, scriptPath: process.execPath, pipeName: "test-pipe", sessionId: "session-1", scope: "workspace-plugin" },
    });
    assert.ok(runtime.mcpMount);
    const registry = JSON.parse(await readFile(registryPath, "utf8"));
    assert.deepEqual(registry.entries, [existing, { path: ".agents/plugins", include_only: [`^${runtime.mcpMount!.serverName}$`] }]);
    await cleanupManagerRuntime(runtime);
    const cleaned = JSON.parse(await readFile(registryPath, "utf8"));
    assert.deepEqual(cleaned.entries, [existing]);
  } finally {
    if (previousRuntime === undefined) delete process.env.ILMATTO_MANAGER_RUNTIME; else process.env.ILMATTO_MANAGER_RUNTIME = previousRuntime;
    if (previousLog === undefined) delete process.env.ILMATTO_MANAGER_LOG_DIR; else process.env.ILMATTO_MANAGER_LOG_DIR = previousLog;
    await rm(workspace, { recursive: true, force: true });
    await rm(runtimeRoot, { recursive: true, force: true });
  }
});

test("unified runtime removes only stale IlMatto entries from the legacy workspace MCP file", async () => {
  const workspace = await mkdtemp(path.join(os.tmpdir(), "ilmatto-unified-legacy-mcp-workspace-"));
  const runtimeRoot = await mkdtemp(path.join(os.tmpdir(), "ilmatto-unified-legacy-mcp-runtime-"));
  const previousRuntime = process.env.ILMATTO_MANAGER_RUNTIME;
  const previousLog = process.env.ILMATTO_MANAGER_LOG_DIR;
  try {
    process.env.ILMATTO_MANAGER_RUNTIME = runtimeRoot;
    process.env.ILMATTO_MANAGER_LOG_DIR = path.join(runtimeRoot, "logs");
    const legacyConfigPath = path.join(workspace, ".agents", "mcp_config.json");
    await mkdir(path.dirname(legacyConfigPath), { recursive: true });
    await writeFile(legacyConfigPath, JSON.stringify({ mcpServers: {
      userServer: { command: "user-mcp" },
      "ilmatto-codex-observation-stale": { command: process.execPath, args: [path.resolve(process.cwd(), "dist", "codex-mcp.js"), "--pipe", "old", "--session-id", "old"], cwd: workspace },
    } }), "utf8");
    const runtime = await ensureUnifiedManagerRuntime(workspace, undefined, {
      mcp: { command: process.execPath, scriptPath: path.resolve(process.cwd(), "dist", "agent-tools-mcp.js"), pipeName: "new", sessionId: "session-1", scope: "workspace-plugin" },
    });
    const cleaned = JSON.parse(await readFile(legacyConfigPath, "utf8"));
    assert.deepEqual(cleaned.mcpServers, { userServer: { command: "user-mcp" } });
    await cleanupManagerRuntime(runtime);
  } finally {
    if (previousRuntime === undefined) delete process.env.ILMATTO_MANAGER_RUNTIME; else process.env.ILMATTO_MANAGER_RUNTIME = previousRuntime;
    if (previousLog === undefined) delete process.env.ILMATTO_MANAGER_LOG_DIR; else process.env.ILMATTO_MANAGER_LOG_DIR = previousLog;
    await rm(workspace, { recursive: true, force: true });
    await rm(runtimeRoot, { recursive: true, force: true });
  }
});

test("invalid workspace MCP plugin config disables the optional mount without breaking runtime setup", async () => {
  const workspace = await mkdtemp(path.join(os.tmpdir(), "ilmatto-unified-invalid-mcp-"));
  const runtimeRoot = await mkdtemp(path.join(os.tmpdir(), "ilmatto-unified-invalid-runtime-"));
  const previousRuntime = process.env.ILMATTO_MANAGER_RUNTIME;
  const previousLog = process.env.ILMATTO_MANAGER_LOG_DIR;
  try {
    process.env.ILMATTO_MANAGER_RUNTIME = runtimeRoot;
    process.env.ILMATTO_MANAGER_LOG_DIR = path.join(runtimeRoot, "logs");
    // The generated name is deterministic; create a malformed config at the
    // path that the mount will use by deriving it from the same inputs.
    const { createHash } = await import("node:crypto");
    const sessionKey = createHash("sha256").update(`${path.resolve(workspace)}\nsession-1`, "utf8").digest("hex").slice(0, 12);
    const generatedDirectory = path.join(workspace, ".agents", "plugins", `ilmatto-agent-tools-${sessionKey}`);
    await mkdir(generatedDirectory, { recursive: true });
    await writeFile(path.join(generatedDirectory, "plugin.json"), JSON.stringify({ name: `ilmatto-agent-tools-${sessionKey}` }), "utf8");
    await writeFile(path.join(generatedDirectory, "mcp_config.json"), JSON.stringify({ mcpServers: [] }), "utf8");
    const runtime = await ensureUnifiedManagerRuntime(workspace, undefined, {
      mcp: { command: process.execPath, scriptPath: process.execPath, pipeName: "test-pipe", sessionId: "session-1", scope: "workspace-plugin" },
    });
    assert.equal(runtime.mcpMount, undefined);
    assert.match(runtime.mcpMountError ?? "", /mcpServers/);
    await cleanupManagerRuntime(runtime);
  } finally {
    if (previousRuntime === undefined) delete process.env.ILMATTO_MANAGER_RUNTIME; else process.env.ILMATTO_MANAGER_RUNTIME = previousRuntime;
    if (previousLog === undefined) delete process.env.ILMATTO_MANAGER_LOG_DIR; else process.env.ILMATTO_MANAGER_LOG_DIR = previousLog;
    await rm(workspace, { recursive: true, force: true });
    await rm(runtimeRoot, { recursive: true, force: true });
  }
});

test("manager runtime exposes no executable tools", async () => {
  const root = await mkdtemp(path.join(os.tmpdir(), "ilmatto-manager-runtime-"));
  const previous = process.env.ILMATTO_MANAGER_RUNTIME;
  const previousLog = process.env.ILMATTO_MANAGER_LOG_DIR;
  const previousAgentDir = process.env.ILMATTO_MANAGER_AGENT_DIR;
  const previousSettings = process.env.ILMATTO_MANAGER_SETTINGS_PATH;
  process.env.ILMATTO_MANAGER_RUNTIME = root;
  process.env.ILMATTO_MANAGER_LOG_DIR = path.join(root, "logs");
  process.env.ILMATTO_MANAGER_AGENT_DIR = path.join(root, "global-agents");
  process.env.ILMATTO_MANAGER_SETTINGS_PATH = path.join(root, "global-settings.json");
  try {
    const runtime = await ensureManagerRuntime();
    await assert.doesNotReject(stat(runtime.attachmentsRoot));
    const agent = await readFile(path.join(runtime.root, ".agents", "agents", `${runtime.agentName}.md`), "utf8");
    const frontmatter = agent.slice(0, agent.indexOf("\n---\n", 4));
    assert.match(agent, /^  - search_web\s*$/m);
    assert.match(agent, /^  - read_url_content\s*$/m);
    assert.match(agent, /^  - invoke_subagent\s*$/m);
    assert.doesNotMatch(frontmatter, /edit_file|run_command|start_subagent|write_file/);
    assert.match(agent, /subagent: true/);
    const workspaceNestedAgent = path.join(runtime.root, ".agents", "agents", runtime.agentName, "agent.md");
    assert.equal(await readFile(workspaceNestedAgent, "utf8"), agent);
    const globalAgent = await readFile(runtime.agentPath, "utf8");
    assert.equal(globalAgent, agent);
    assert.equal(await readFile(path.join(path.dirname(runtime.agentPath), runtime.agentName, "agent.md"), "utf8"), agent);
    const settings = JSON.parse(await readFile(path.join(runtime.root, ".gemini", "settings.json"), "utf8"));
    assert.equal(settings.altScreenMode, "never");
    assert.equal(settings.verbosity, "low");
    assert.deepEqual(settings.permissions.allow, buildManagerPermissionRules(runtime.attachmentsRoot));
    assert.ok(runtime.globalSettingsPath);
    const globalSettings = JSON.parse(await readFile(runtime.globalSettingsPath, "utf8"));
    assert.deepEqual(globalSettings.permissions.allow, buildManagerPermissionRules(runtime.attachmentsRoot));
    assert.deepEqual(globalSettings.permissions.allow.slice(-buildCompanionWebPermissionRules().length), buildCompanionWebPermissionRules());
    await cleanupManagerRuntime(runtime);
    await assert.rejects(stat(runtime.agentPath));
    await assert.rejects(stat(workspaceNestedAgent));
    await assert.rejects(stat(path.join(path.dirname(runtime.agentPath), runtime.agentName, "agent.md")));
    assert.deepEqual(managerActionSchema.properties.action.enum, ["delegate_code", "respond", "ask_user"]);
  } finally {
    if (previous === undefined) delete process.env.ILMATTO_MANAGER_RUNTIME; else process.env.ILMATTO_MANAGER_RUNTIME = previous;
    if (previousLog === undefined) delete process.env.ILMATTO_MANAGER_LOG_DIR; else process.env.ILMATTO_MANAGER_LOG_DIR = previousLog;
    if (previousAgentDir === undefined) delete process.env.ILMATTO_MANAGER_AGENT_DIR; else process.env.ILMATTO_MANAGER_AGENT_DIR = previousAgentDir;
    if (previousSettings === undefined) delete process.env.ILMATTO_MANAGER_SETTINGS_PATH; else process.env.ILMATTO_MANAGER_SETTINGS_PATH = previousSettings;
    await rm(root, { recursive: true, force: true });
  }
});

test("manager runtime removes the legacy fixed-name agent before creating a session", async () => {
  const root = await mkdtemp(path.join(os.tmpdir(), "ilmatto-manager-legacy-agent-"));
  const previous = process.env.ILMATTO_MANAGER_RUNTIME;
  const previousLog = process.env.ILMATTO_MANAGER_LOG_DIR;
  const previousAgentDir = process.env.ILMATTO_MANAGER_AGENT_DIR;
  const previousSettings = process.env.ILMATTO_MANAGER_SETTINGS_PATH;
  process.env.ILMATTO_MANAGER_RUNTIME = root;
  process.env.ILMATTO_MANAGER_LOG_DIR = path.join(root, "logs");
  process.env.ILMATTO_MANAGER_AGENT_DIR = path.join(root, "global-agents");
  process.env.ILMATTO_MANAGER_SETTINGS_PATH = path.join(root, "global-settings.json");
  try {
    const legacy = path.join(root, ".agents", "agents", "ilmatto-manager", "agent.md");
    await mkdir(path.dirname(legacy), { recursive: true });
    await writeFile(legacy, "---\nname: ilmatto-manager\ntools: []\n---\n", "utf8");
    const runtime = await ensureManagerRuntime();
    await assert.rejects(stat(legacy));
    await cleanupManagerRuntime(runtime);
  } finally {
    if (previous === undefined) delete process.env.ILMATTO_MANAGER_RUNTIME; else process.env.ILMATTO_MANAGER_RUNTIME = previous;
    if (previousLog === undefined) delete process.env.ILMATTO_MANAGER_LOG_DIR; else process.env.ILMATTO_MANAGER_LOG_DIR = previousLog;
    if (previousAgentDir === undefined) delete process.env.ILMATTO_MANAGER_AGENT_DIR; else process.env.ILMATTO_MANAGER_AGENT_DIR = previousAgentDir;
    if (previousSettings === undefined) delete process.env.ILMATTO_MANAGER_SETTINGS_PATH; else process.env.ILMATTO_MANAGER_SETTINGS_PATH = previousSettings;
    await rm(root, { recursive: true, force: true });
  }
});

test("manager runtime grants only read-only view_file access for managed images", async () => {
  const root = await mkdtemp(path.join(os.tmpdir(), "ilmatto-manager-image-runtime-"));
  const previous = process.env.ILMATTO_MANAGER_RUNTIME;
  const previousLog = process.env.ILMATTO_MANAGER_LOG_DIR;
  const previousAgentDir = process.env.ILMATTO_MANAGER_AGENT_DIR;
  const previousSettings = process.env.ILMATTO_MANAGER_SETTINGS_PATH;
  process.env.ILMATTO_MANAGER_RUNTIME = root;
  process.env.ILMATTO_MANAGER_LOG_DIR = path.join(root, "logs");
  process.env.ILMATTO_MANAGER_AGENT_DIR = path.join(root, "global-agents");
  process.env.ILMATTO_MANAGER_SETTINGS_PATH = path.join(root, "global-settings.json");
  try {
    const runtime = await ensureManagerRuntime();
    const agent = await readFile(path.join(runtime.root, ".agents", "agents", `${runtime.agentName}.md`), "utf8");
    const frontmatter = agent.slice(0, agent.indexOf("\n---\n", 4));
    assert.match(agent, /^  - search_web\s*$/m);
    assert.doesNotMatch(frontmatter, /run_command|write_to_file|replace_file_content|start_subagent/);
    assert.match(agent, /managed image paths/i);
    await cleanupManagerRuntime(runtime);
  } finally {
    if (previous === undefined) delete process.env.ILMATTO_MANAGER_RUNTIME; else process.env.ILMATTO_MANAGER_RUNTIME = previous;
    if (previousLog === undefined) delete process.env.ILMATTO_MANAGER_LOG_DIR; else process.env.ILMATTO_MANAGER_LOG_DIR = previousLog;
    if (previousAgentDir === undefined) delete process.env.ILMATTO_MANAGER_AGENT_DIR; else process.env.ILMATTO_MANAGER_AGENT_DIR = previousAgentDir;
    if (previousSettings === undefined) delete process.env.ILMATTO_MANAGER_SETTINGS_PATH; else process.env.ILMATTO_MANAGER_SETTINGS_PATH = previousSettings;
    await rm(root, { recursive: true, force: true });
  }
});
