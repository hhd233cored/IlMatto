import os from "node:os";
import path from "node:path";
import { createHash } from "node:crypto";
import { existsSync } from "node:fs";
import { mkdir, readFile, readdir, rm, writeFile } from "node:fs/promises";
import { buildCompanionSystemPrompt, defaultCompanionProfile } from "./companion.js";
import type { CompanionProfile } from "./protocol.js";
import { safeSessionId } from "./companion-memory.js";

/** Names used by the generic IlMatto Agent Tools MCP. The legacy Codex-only
 * names remain recognized so an upgrade can remove stale generated entries
 * without touching user-owned MCP servers. */
const CURRENT_MCP_PREFIX = "ilmatto-agent-tools-";
const LEGACY_MCP_PREFIX = "ilmatto-codex-observation-";
const LEGACY_GLOBAL_MCP_NAME = "ilmatto-codex-observation";
let mcpConfigQueue: Promise<void> = Promise.resolve();

async function withMcpConfigLock<T>(operation: () => Promise<T>): Promise<T> {
  const previous = mcpConfigQueue;
  let release!: () => void;
  mcpConfigQueue = new Promise<void>((resolve) => { release = resolve; });
  await previous;
  try { return await operation(); }
  finally { release(); }
}

function isGeneratedWorkspaceMcpName(name: string): boolean {
  return name.startsWith(CURRENT_MCP_PREFIX) || name.startsWith(LEGACY_MCP_PREFIX);
}

export type ManagerRuntime = {
  root: string;
  /** Application-local companion profile and per-conversation memory root. */
  memoryRoot?: string;
  /** Per-session copies used when Antigravity reads an image by path. */
  attachmentsRoot: string;
  schemaPath: string;
  logPath: string;
  /** The AGY settings file that carries managed-image and web permissions. */
  globalSettingsPath?: string;
  /** A per-runtime name prevents collisions with the user's own AGY agents. */
  agentName: string;
  /** The generated global agent definition used by AGY CLI discovery. */
  agentPath: string;
  /** Temporary IlMatto Agent Tools MCP entry created for this Manager session. */
  mcpMount?: ManagerMcpMount;
  /** Optional diagnostic when the MCP entry could not be mounted. */
  mcpMountError?: string;
};

export type ManagerMcpMount = {
  configPath: string;
  /** Where Antigravity reads the temporary MCP entry. */
  scope?: "global" | "workspace-plugin";
  manifestPath?: string;
  pluginDirectory?: string;
  /** Explicit workspace registration used by AGY builds that do not scan
   * `.agents/plugins` until it is listed in `.agents/plugins.json`. */
  pluginRegistryPath?: string;
  pluginRegistryEntry?: Record<string, unknown>;
  pluginRegistryEntryCreated?: boolean;
  serverName: string;
  definition: Record<string, unknown>;
  manifest?: Record<string, unknown>;
};

export type UnifiedManagerRuntimeOptions = {
  mcp?: {
    command: string;
    scriptPath: string;
    pipeName: string;
    sessionId: string;
    /** Defaults to the user's Antigravity global MCP config. */
    scope?: "global" | "workspace-plugin";
    /** Test/diagnostic override for the global config path. */
    configPath?: string;
  };
};

/**
 * The unified Manager runtime deliberately contains no generated Agent
 * definition or permission file. `root` is the user-selected workspace so
 * the single Antigravity process can operate on the same files the user sees.
 * A temporary MCP entry is the only optional customization; it is removed on
 * shutdown when it still matches the definition IlMatto wrote. The legacy
 * fields remain in the type for old callers/tests and are now only diagnostic
 * placeholders.
 */

/** Legacy policy data retained for old callers/tests. The unified Manager does
 * not generate an Agent from this list and does not apply these restrictions. */
export const companionAgentTools = ["view_file", "search_web", "read_url_content", "invoke_subagent"] as const;

export const managerActionSchema = {
  type: "object",
  additionalProperties: false,
  properties: {
    schemaVersion: { type: "integer", const: 1 },
    action: { type: "string", enum: ["delegate_code", "respond", "ask_user"] },
    message: { type: "string" },
  },
  required: ["schemaVersion", "action", "message"],
} as const;

/**
 * AGY 1.1.x reports the built-in read-only file tool as `readfile`, while
 * newer builds/documentation use `read_file`.  The rules are deliberately
 * scoped to IlMatto's runtime attachment directory and never grant access to
 * the workspace or arbitrary user paths.
 */
export function buildManagedImageReadPermissionRules(attachmentsRoot: string): string[] {
  const target = path.resolve(attachmentsRoot).replace(/[\\]+/g, "/").replace(/\/+$/, "");
  return [`readfile(${target})`, `read_file(${target})`];
}

/** Web research and browser navigation are intentionally separate from local
 * file permissions. `read_url` covers page reads/search network access, while
 * `execute_url` covers browser interaction such as clicking and typing. */
export function buildCompanionWebPermissionRules(): string[] {
  return ["read_url(*)", "execute_url(*)"];
}

export function buildManagerPermissionRules(attachmentsRoot: string): string[] {
  return [...buildManagedImageReadPermissionRules(attachmentsRoot), ...buildCompanionWebPermissionRules()];
}

async function allowManagedImageReads(settingsPath: string, attachmentsRoot: string): Promise<void> {
  let settings: Record<string, any> = {};
  try {
    const current = JSON.parse(await readFile(settingsPath, "utf8"));
    if (!current || typeof current !== "object" || Array.isArray(current)) throw new Error("settings.json root must be an object");
    settings = current;
  } catch (error: any) {
    if (error?.code !== "ENOENT") throw new Error(`无法读取 Antigravity settings.json：${error instanceof Error ? error.message : "文件格式无效"}`);
  }
  const existingPermissions = settings.permissions;
  if (existingPermissions !== undefined && (!existingPermissions || typeof existingPermissions !== "object" || Array.isArray(existingPermissions))) {
    throw new Error("Antigravity settings.json 的 permissions 配置格式无效。");
  }
  const existingAllow = existingPermissions?.allow;
  if (existingAllow !== undefined && !Array.isArray(existingAllow)) {
    throw new Error("Antigravity settings.json 的 permissions.allow 必须是数组。");
  }
  const rules = buildManagerPermissionRules(attachmentsRoot);
  const allow = (existingAllow ?? []).filter((item: unknown): item is string => typeof item === "string");
  for (const rule of rules) if (!allow.includes(rule)) allow.push(rule);
  settings.permissions = { ...(existingPermissions ?? {}), allow };
  await mkdir(path.dirname(settingsPath), { recursive: true });
  await writeFile(settingsPath, JSON.stringify(settings, null, 2), "utf8");
}

/** Kept as the default prompt export for existing coordinator tests/callers. */
export const managerPolicyPrompt = buildCompanionSystemPrompt(defaultCompanionProfile);

function renderManagerAgent(agentName: string, systemPrompt: string): string {
  const tools = companionAgentTools.map((tool) => `  - ${tool}`).join("\n");
  return `---
name: ${agentName}
description: IlMatto policy-limited general assistant and coding-task router.
tools:
${tools}
mainAgent: true
subagent: true
commandExecutionPolicy: off
skills: []
plugins: []
---

# IlMatto Manager Policy

${systemPrompt}
`;
}

export async function ensureManagerRuntime(profile?: CompanionProfile): Promise<ManagerRuntime> {
  // Backward-compatible helper for older callers and tests. The unified
  // Manager never calls this function; it uses ensureUnifiedManagerRuntime,
  // which does not generate any AGY files.
  const localAppData = process.env.LOCALAPPDATA ?? path.join(os.homedir(), "AppData", "Local");
  const memoryRoot = path.resolve(process.env.ILMATTO_COMPANION_MEMORY_DIR ?? path.join(localAppData, "IlMatto", "companion-memory"));
  const root = path.resolve(process.env.ILMATTO_MANAGER_RUNTIME ?? path.join(localAppData, "IlMatto", "manager-runtime"));
  const agentsRoot = path.join(root, ".agents", "agents");
  const logDirectory = path.resolve(process.env.ILMATTO_MANAGER_LOG_DIR ?? path.join(localAppData, "IlMatto", "logs"));
  const globalAgentsRoot = path.resolve(process.env.ILMATTO_MANAGER_AGENT_DIR ?? path.join(os.homedir(), ".gemini", "config", "agents"));
  const fingerprint = createHash("sha256").update(root.toLowerCase()).digest("hex").slice(0, 12);
  const agentName = `ilmatto-manager-${fingerprint}`;
  await mkdir(agentsRoot, { recursive: true });
  await mkdir(logDirectory, { recursive: true });
  await mkdir(globalAgentsRoot, { recursive: true });
  const schemaPath = path.join(root, "manager-action.schema.json");
  const projectSettingsDirectory = path.join(root, ".gemini");
  const projectSettingsPath = path.join(projectSettingsDirectory, "settings.json");
  const attachmentsRoot = path.join(root, "attachments");
  const globalSettingsPath = path.resolve(process.env.ILMATTO_MANAGER_SETTINGS_PATH ?? path.join(globalAgentsRoot, "..", "..", "antigravity-cli", "settings.json"));
  const localAgentPath = path.join(agentsRoot, `${agentName}.md`);
  const nestedAgentDirectory = path.join(agentsRoot, agentName);
  const localNestedAgentPath = path.join(nestedAgentDirectory, "agent.md");
  const agentPath = path.join(globalAgentsRoot, `${agentName}.md`);
  const globalNestedAgentPath = path.join(globalAgentsRoot, agentName, "agent.md");
  const legacyAgentDirectory = path.join(agentsRoot, "ilmatto-manager");
  const logPath = path.join(logDirectory, "antigravity-manager.log");
  await mkdir(projectSettingsDirectory, { recursive: true });
  await writeFile(projectSettingsPath, JSON.stringify({
    altScreenMode: "never", verbosity: "low", enableTerminalSandbox: true,
    allowNonWorkspaceAccess: false, toolPermission: "strict",
    permissions: { allow: buildManagerPermissionRules(attachmentsRoot) },
  }, null, 2), "utf8");
  await allowManagedImageReads(globalSettingsPath, attachmentsRoot);
  await writeFile(schemaPath, JSON.stringify(managerActionSchema, null, 2), "utf8");
  const agentDefinition = renderManagerAgent(agentName, buildCompanionSystemPrompt(profile));
  await mkdir(nestedAgentDirectory, { recursive: true });
  await writeFile(localAgentPath, agentDefinition, "utf8");
  await writeFile(localNestedAgentPath, agentDefinition, "utf8");
  await mkdir(path.dirname(globalNestedAgentPath), { recursive: true });
  await writeFile(agentPath, agentDefinition, "utf8");
  await writeFile(globalNestedAgentPath, agentDefinition, "utf8");
  await rm(legacyAgentDirectory, { recursive: true, force: true });
  await mkdir(attachmentsRoot, { recursive: true });
  return { root, memoryRoot, attachmentsRoot, schemaPath, logPath, globalSettingsPath, agentName, agentPath };
}

export async function ensureUnifiedManagerRuntime(workspacePath?: string, _profile?: CompanionProfile, options?: UnifiedManagerRuntimeOptions): Promise<ManagerRuntime> {
  const localAppData = process.env.LOCALAPPDATA ?? path.join(os.homedir(), "AppData", "Local");
  const memoryRoot = path.resolve(process.env.ILMATTO_COMPANION_MEMORY_DIR ?? path.join(localAppData, "IlMatto", "companion-memory"));
  const configuredRuntime = path.resolve(process.env.ILMATTO_MANAGER_RUNTIME ?? path.join(localAppData, "IlMatto", "manager-runtime"));
  const root = workspacePath ? path.resolve(workspacePath) : configuredRuntime;
  const logDirectory = path.resolve(process.env.ILMATTO_MANAGER_LOG_DIR ?? path.join(localAppData, "IlMatto", "logs"));
  const fingerprint = createHash("sha256").update(root.toLowerCase()).digest("hex").slice(0, 12);
  const legacyFingerprint = createHash("sha256").update(configuredRuntime.toLowerCase()).digest("hex").slice(0, 12);
  // When an override points the runtime at the selected workspace, avoid
  // deleting legacy Agent/Schema artifacts there; the workspace may contain
  // user-owned .agents. The temporary MCP entry is handled separately below.
  if (root.toLowerCase() !== configuredRuntime.toLowerCase()) {
    await cleanupLegacyArtifacts(configuredRuntime, legacyFingerprint, logDirectory);
  }
  await mkdir(logDirectory, { recursive: true });
  const attachmentsRoot = path.join(configuredRuntime, "attachments");
  const logPath = path.join(logDirectory, `antigravity-manager-${fingerprint}.log`);
  await mkdir(attachmentsRoot, { recursive: true });
  let mcpMount: ManagerMcpMount | undefined;
  let mcpMountError: string | undefined;
  if (options?.mcp) {
    try {
      // Remove only entries that older ManagerHost versions generated in the
      // workspace. The active entry is now written to the global AGY config.
      await cleanupLegacyWorkspaceMcp(root, options.mcp);
      await cleanupLegacyWorkspaceMcpPlugins(root, options.mcp);
      mcpMount = options.mcp.scope === "workspace-plugin"
        ? await mountWorkspaceMcp(root, options.mcp)
        : await mountGlobalMcp(options.mcp);
    } catch (error) {
      // MCP is optional. A read-only/invalid workspace config must not stop
      // the unified Antigravity session from starting.
      mcpMountError = error instanceof Error ? error.message : "无法挂载 IlMatto Agent Tools MCP。";
    }
  }
  return {
    root,
    memoryRoot,
    attachmentsRoot,
    // Kept as an empty compatibility field so the unified process cannot
    // accidentally opt into structured output through a stale path.
    schemaPath: "",
    logPath,
    agentName: "",
    agentPath: "",
    mcpMount,
    mcpMountError,
  };
}

async function cleanupLegacyWorkspaceMcp(workspacePath: string, options: NonNullable<UnifiedManagerRuntimeOptions["mcp"]>): Promise<void> {
  const configPath = path.join(workspacePath, ".agents", "mcp_config.json");
  try {
    const parsed = JSON.parse(await readFile(configPath, "utf8"));
    if (!parsed || typeof parsed !== "object" || Array.isArray(parsed)) return;
    const config = parsed as Record<string, unknown>;
    const servers = config.mcpServers;
    if (!servers || typeof servers !== "object" || Array.isArray(servers)) return;
    const scriptPath = path.resolve(options.scriptPath);
    const remaining = { ...(servers as Record<string, unknown>) };
    let changed = false;
    for (const [name, value] of Object.entries(remaining)) {
      if (!isGeneratedWorkspaceMcpName(name) || !value || typeof value !== "object" || Array.isArray(value)) continue;
      const entry = value as Record<string, unknown>;
      const args = entry.args;
      const isGenerated = entry.command === path.resolve(options.command) && Array.isArray(args) && args[0] === scriptPath && args.includes("--session-id");
      if (isGenerated) { delete remaining[name]; changed = true; }
    }
    if (!changed) return;
    if (Object.keys(remaining).length === 0 && Object.keys(config).every((key) => key === "mcpServers")) await rm(configPath, { force: true });
    else await writeFile(configPath, JSON.stringify({ ...config, mcpServers: remaining }, null, 2), "utf8");
  } catch {
    // A malformed or inaccessible legacy file must never disable the new
    // plugin mount or the unified Antigravity session.
  }
}

/** Remove stale workspace plugins created by pre-global-mount ManagerHost
 * versions. Only the exact generated plugin shape and its exact registry
 * entry are removed; user-owned plugins and modified definitions remain. */
async function cleanupLegacyWorkspaceMcpPlugins(workspacePath: string, options: NonNullable<UnifiedManagerRuntimeOptions["mcp"]>): Promise<void> {
  const pluginsRoot = path.join(workspacePath, ".agents", "plugins");
  const registryPath = path.join(workspacePath, ".agents", "plugins.json");
  let pluginNames: string[];
  try { pluginNames = (await readdir(pluginsRoot, { withFileTypes: true })).filter((entry) => entry.isDirectory() && isGeneratedWorkspaceMcpName(entry.name)).map((entry) => entry.name); }
  catch { return; }
  const staleNames: string[] = [];
  for (const pluginName of pluginNames) {
    const pluginDirectory = path.join(pluginsRoot, pluginName);
    try {
      const manifest = JSON.parse(await readFile(path.join(pluginDirectory, "plugin.json"), "utf8"));
      const config = JSON.parse(await readFile(path.join(pluginDirectory, "mcp_config.json"), "utf8"));
      const servers = config?.mcpServers;
      const values = servers && typeof servers === "object" && !Array.isArray(servers) ? Object.values(servers as Record<string, unknown>) : [];
      if (manifest?.name !== pluginName || values.length !== 1 || !isIlMattoGeneratedDefinition(values[0], options)) continue;
      await rm(pluginDirectory, { recursive: true, force: true });
      staleNames.push(pluginName);
    } catch { /* malformed or user-modified plugins are left untouched */ }
  }
  if (staleNames.length === 0) return;
  try {
    const parsed = JSON.parse(await readFile(registryPath, "utf8"));
    if (!parsed || typeof parsed !== "object" || Array.isArray(parsed) || !Array.isArray(parsed.entries)) return;
    const relativePath = path.relative(workspacePath, pluginsRoot).replace(/[\\]+/g, "/");
    const remaining = parsed.entries.filter((item: any) => {
      if (!item || typeof item !== "object" || item.path !== relativePath || !Array.isArray(item.include_only) || item.include_only.length !== 1 || typeof item.include_only[0] !== "string") return true;
      return !staleNames.some((name) => item.include_only[0] === `^${escapeRegex(name)}$`);
    });
    if (remaining.length === parsed.entries.length) return;
    if (remaining.length === 0 && Object.keys(parsed).every((key) => key === "entries")) await rm(registryPath, { force: true });
    else await writeFile(registryPath, JSON.stringify({ ...parsed, entries: remaining }, null, 2), "utf8");
  } catch { /* best-effort cleanup only */ }
}

/**
 * Compatibility path: add one uniquely-owned stdio MCP plugin to the active
 * workspace. New sessions use mountGlobalMcp by default because the Windows
 * headless CLI did not ingest the generated workspace registration reliably.
 *
 * The Antigravity CLI version currently distributed on Windows documents only
 * global MCP config and plugin-local MCP config. It does not load a bare
 * `<workspace>/.agents/mcp_config.json`, even though that path is accepted by
 * some newer Antigravity surfaces. A workspace plugin is therefore the
 * narrowest supported session-scoped mount: it is discovered from
 * `<workspace>/.agents/plugins/<name>/` and does not touch the user's global
 * configuration. Every generated file is removed on shutdown only if it is
 * still equivalent to what IlMatto wrote.
 */
async function mountWorkspaceMcp(workspacePath: string, options: NonNullable<UnifiedManagerRuntimeOptions["mcp"]>): Promise<ManagerMcpMount> {
  if (!existsSync(options.scriptPath)) throw new Error(`找不到 IlMatto Agent Tools MCP：${options.scriptPath}`);
  const agentsRoot = path.join(workspacePath, ".agents");
  const pluginsRoot = path.join(agentsRoot, "plugins");
  const sessionKey = createHash("sha256").update(`${workspacePath}\n${options.sessionId}`, "utf8").digest("hex").slice(0, 12);
  const serverName = `${CURRENT_MCP_PREFIX}${sessionKey}`;
  const pluginDirectory = path.join(pluginsRoot, serverName);
  const manifestPath = path.join(pluginDirectory, "plugin.json");
  const configPath = path.join(pluginDirectory, "mcp_config.json");
  const pluginRegistryPath = path.join(agentsRoot, "plugins.json");
  // `plugins.json` entries point to a directory containing plugin folders and
  // are resolved relative to the workspace root by AGY, not relative to
  // `.agents/`. Restrict the entry to this generated plugin so an explicit
  // registration never changes which user plugins are loaded.
  const pluginRegistryEntry: Record<string, unknown> = {
    path: path.relative(workspacePath, pluginsRoot).replace(/[\\]+/g, "/"),
    include_only: [`^${escapeRegex(serverName)}$`],
  };
  const manifest: Record<string, unknown> = { name: serverName };
  let existingManifest: Record<string, unknown> | undefined;
  try {
    const parsed = JSON.parse(await readFile(manifestPath, "utf8"));
    if (!parsed || typeof parsed !== "object" || Array.isArray(parsed)) throw new Error("IlMatto MCP plugin.json 根节点必须是对象。");
    existingManifest = parsed as Record<string, unknown>;
  } catch (error: any) {
    if (error?.code !== "ENOENT") throw new Error(`无法读取 IlMatto MCP plugin.json：${error instanceof Error ? error.message : "文件格式无效"}`);
  }
  if (existingManifest && !deepEqual(existingManifest, manifest)) throw new Error(`IlMatto MCP plugin 名称已被占用：${serverName}`);
  let config: Record<string, unknown> = {};
  try {
    const parsed = JSON.parse(await readFile(configPath, "utf8"));
    if (!parsed || typeof parsed !== "object" || Array.isArray(parsed)) throw new Error("IlMatto MCP mcp_config.json 根节点必须是对象。");
    config = parsed as Record<string, unknown>;
  } catch (error: any) {
    if (error?.code !== "ENOENT") throw new Error(`无法读取 IlMatto MCP 配置：${error instanceof Error ? error.message : "文件格式无效"}`);
  }
  const existingServers = config.mcpServers;
  if (existingServers !== undefined && (!existingServers || typeof existingServers !== "object" || Array.isArray(existingServers))) {
    throw new Error("IlMatto MCP mcp_config.json 的 mcpServers 必须是对象。");
  }
  const definition: Record<string, unknown> = {
    command: path.resolve(options.command),
    args: [path.resolve(options.scriptPath), "--pipe", options.pipeName, "--session-id", options.sessionId],
  };
  const servers = { ...((existingServers as Record<string, unknown> | undefined) ?? {}) };
  const previous = servers[serverName];
  if (previous !== undefined && !deepEqual(previous, definition) && !isIlMattoMcpDefinition(previous, options)) {
    throw new Error(`IlMatto MCP Server 名称已被占用：${serverName}`);
  }
  servers[serverName] = definition;
  await mkdir(pluginDirectory, { recursive: true });
  await writeFile(manifestPath, JSON.stringify(manifest, null, 2), "utf8");
  await writeFile(configPath, JSON.stringify({ ...config, mcpServers: servers }, null, 2), "utf8");
  const pluginRegistryEntryCreated = await ensureWorkspacePluginRegistration(pluginRegistryPath, pluginRegistryEntry, serverName);
  return { configPath, scope: "workspace-plugin", manifestPath, pluginDirectory, pluginRegistryPath, pluginRegistryEntry, pluginRegistryEntryCreated, serverName, definition, manifest };
}

/**
 * Explicitly register the generated plugin for AGY versions whose workspace
 * scanner only loads plugin directories listed in `.agents/plugins.json`.
 * Existing entries and inherited configuration are preserved verbatim.
 */
async function ensureWorkspacePluginRegistration(registryPath: string, entry: Record<string, unknown>, pluginName: string): Promise<boolean> {
  let registry: Record<string, unknown> = {};
  try {
    const parsed = JSON.parse(await readFile(registryPath, "utf8"));
    if (!parsed || typeof parsed !== "object" || Array.isArray(parsed)) throw new Error("plugins.json 根节点必须是对象。");
    registry = parsed as Record<string, unknown>;
  } catch (error: any) {
    if (error?.code !== "ENOENT") throw new Error(`无法读取 Antigravity plugins.json：${error instanceof Error ? error.message : "文件格式无效"}`);
  }
  const existingEntries = registry.entries;
  if (existingEntries !== undefined && (!Array.isArray(existingEntries) || existingEntries.some((item) => !item || typeof item !== "object" || Array.isArray(item)))) {
    throw new Error("Antigravity plugins.json 的 entries 必须是对象数组。");
  }
  const entries = [...((existingEntries as Array<Record<string, unknown>> | undefined) ?? [])];
  const duplicate = entries.find((item) => item.path === entry.path && registrationIncludesPlugin(item, pluginName));
  if (duplicate) {
    // A user-owned registration is already sufficient; do not claim ownership
    // so shutdown cannot remove it.
    return false;
  }
  entries.push(entry);
  await mkdir(path.dirname(registryPath), { recursive: true });
  await writeFile(registryPath, JSON.stringify({ ...registry, entries }, null, 2), "utf8");
  return true;
}

function registrationIncludesPlugin(entry: Record<string, unknown>, pluginName: string): boolean {
  const filters = entry.include_only;
  if (filters === undefined) return true;
  if (!Array.isArray(filters)) return false;
  return filters.some((filter) => {
    if (typeof filter !== "string") return false;
    try { return new RegExp(filter).test(pluginName); } catch { return false; }
  });
}

function escapeRegex(value: string): string {
  return value.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}

/**
 * Add the generic Agent Tools Facade to Antigravity's user-level MCP configuration.
 * The entry is deliberately temporary: it points at the current ManagerHost
 * named pipe and is removed on normal shutdown if it has not been edited by
 * the user. A session-scoped server name prevents multiple ManagerHost
 * instances from claiming the same MCP entry in the global file.
 */
async function mountGlobalMcp(options: NonNullable<UnifiedManagerRuntimeOptions["mcp"]>): Promise<ManagerMcpMount> {
  return withMcpConfigLock(() => mountGlobalMcpUnlocked(options));
}

async function mountGlobalMcpUnlocked(options: NonNullable<UnifiedManagerRuntimeOptions["mcp"]>): Promise<ManagerMcpMount> {
  if (!existsSync(options.scriptPath)) throw new Error(`找不到 IlMatto Agent Tools MCP：${options.scriptPath}`);
  const defaultPath = path.join(os.homedir(), ".gemini", "config", "mcp_config.json");
  const configPath = path.resolve(options.configPath ?? process.env.ILMATTO_MANAGER_MCP_CONFIG_PATH ?? defaultPath);
  const sessionKey = createHash("sha256").update(options.sessionId, "utf8").digest("hex").slice(0, 12);
  const serverName = `${CURRENT_MCP_PREFIX}${sessionKey}`;
  let config: Record<string, unknown> = {};
  try {
    const parsed = JSON.parse(await readFile(configPath, "utf8"));
    if (!parsed || typeof parsed !== "object" || Array.isArray(parsed)) throw new Error("Antigravity 全局 mcp_config.json 根节点必须是对象。");
    config = parsed as Record<string, unknown>;
  } catch (error: any) {
    if (error?.code !== "ENOENT") throw new Error(`无法读取 Antigravity 全局 mcp_config.json：${error instanceof Error ? error.message : "文件格式无效"}`);
  }
  const existingServers = config.mcpServers;
  if (existingServers !== undefined && (!existingServers || typeof existingServers !== "object" || Array.isArray(existingServers))) {
    throw new Error("Antigravity 全局 mcp_config.json 的 mcpServers 必须是对象。");
  }
  const definition: Record<string, unknown> = {
    command: path.resolve(options.command),
    args: [path.resolve(options.scriptPath), "--pipe", options.pipeName, "--session-id", options.sessionId],
  };
  const servers = { ...((existingServers as Record<string, unknown> | undefined) ?? {}) };
  // Remove only a stale legacy entry that was generated by IlMatto. A user
  // owned server with the old name is preserved and the new generic name is
  // used alongside it.
  const legacyDefinition = servers[LEGACY_GLOBAL_MCP_NAME];
  if (legacyDefinition !== undefined && isIlMattoGeneratedDefinition(legacyDefinition, options)) delete servers[LEGACY_GLOBAL_MCP_NAME];
  const previous = servers[serverName];
  if (previous !== undefined && !deepEqual(previous, definition) && !isIlMattoGeneratedDefinition(previous, options)) {
    throw new Error(`Antigravity 全局 MCP Server 名称已被占用：${serverName}`);
  }
  servers[serverName] = definition;
  await mkdir(path.dirname(configPath), { recursive: true });
  await writeFile(configPath, JSON.stringify({ ...config, mcpServers: servers }, null, 2), "utf8");
  return { configPath, scope: "global", serverName, definition };
}

function isIlMattoMcpDefinition(value: unknown, options: NonNullable<UnifiedManagerRuntimeOptions["mcp"]>): boolean {
  if (!value || typeof value !== "object" || Array.isArray(value)) return false;
  const entry = value as Record<string, unknown>;
  const args = entry.args;
  if (entry.command !== path.resolve(options.command) || !Array.isArray(args) || args[0] !== path.resolve(options.scriptPath)) return false;
  const sessionIndex = args.indexOf("--session-id");
  return sessionIndex >= 0 && args[sessionIndex + 1] === options.sessionId;
}

/** Recognize an older/stale IlMatto entry so a restarted Manager can safely
 * refresh its named pipe and session id without overwriting user servers. */
function isIlMattoGeneratedDefinition(value: unknown, options: NonNullable<UnifiedManagerRuntimeOptions["mcp"]>): boolean {
  if (!value || typeof value !== "object" || Array.isArray(value)) return false;
  const entry = value as Record<string, unknown>;
  const args = entry.args;
  return entry.command === path.resolve(options.command) && Array.isArray(args) && args[0] === path.resolve(options.scriptPath) && args.includes("--session-id");
}

/** Remove only files that older IlMatto ManagerHost versions generated.  The
 * unified runtime must not touch the selected workspace or arbitrary user
 * Agent definitions, so every target below is a fixed child of the managed
 * runtime (or the exact fingerprinted IlMatto Agent name). */
async function cleanupLegacyArtifacts(configuredRuntime: string, fingerprint: string, logDirectory: string): Promise<void> {
  const agentName = `ilmatto-manager-${fingerprint}`;
  const globalAgentsRoot = path.resolve(process.env.ILMATTO_MANAGER_AGENT_DIR ?? path.join(os.homedir(), ".gemini", "config", "agents"));
  const targets: Array<{ target: string; recursive?: boolean }> = [
    { target: path.join(configuredRuntime, "manager-action.schema.json") },
    { target: path.join(configuredRuntime, ".agents", "agents", `${agentName}.md`) },
    { target: path.join(configuredRuntime, ".agents", "agents", agentName), recursive: true },
    { target: path.join(configuredRuntime, ".agents", "agents", "ilmatto-manager"), recursive: true },
    { target: path.join(globalAgentsRoot, `${agentName}.md`) },
    { target: path.join(globalAgentsRoot, agentName), recursive: true },
    { target: path.join(logDirectory, "antigravity-manager.log") },
  ];
  await Promise.all(targets.map(({ target, recursive }) => rm(target, { force: true, recursive: Boolean(recursive) }).catch(() => undefined)));
  // Do not remove the parent directories: they may contain user-managed
  // agents or other diagnostic files.
}

/** Remove only the generated files; stale files are harmless but cleanup keeps
 * the user's global AGY customization directory tidy after shutdown. */
export async function cleanupManagerRuntime(runtime: ManagerRuntime | undefined): Promise<void> {
  if (!runtime) return;
  if (runtime.mcpMount) await cleanupMcp(runtime.mcpMount);
  // Unified runtimes have no generated Agent/Schema files. Legacy runtimes
  // still receive narrowly-scoped cleanup for the files this module created.
  if (runtime.agentPath) try { await rm(runtime.agentPath, { force: true }); } catch { }
  if (runtime.agentName) {
    try { await rm(path.join(path.dirname(runtime.agentPath), runtime.agentName), { recursive: true, force: true }); } catch { }
    try { await rm(path.join(runtime.root, ".agents", "agents", `${runtime.agentName}.md`), { force: true }); } catch { }
    try { await rm(path.join(runtime.root, ".agents", "agents", runtime.agentName), { recursive: true, force: true }); } catch { }
  }
}

/**
 * Remove the IlMatto-owned, session-scoped files while intentionally leaving
 * the Antigravity CLI conversation untouched. This includes the shared
 * memory store's per-session directory, the desktop's copied attachments, and
 * the runtime copies consumed by Antigravity. The global profile.md and all
 * user-owned runtime/configuration files remain in place.
 */
export async function cleanupManagerSessionData(runtime: ManagerRuntime | undefined, sessionId: string): Promise<void> {
  const safeId = safeSessionId(sessionId);
  const localAppData = process.env.LOCALAPPDATA ?? path.join(os.homedir(), "AppData", "Local");
  const configuredRuntime = path.resolve(process.env.ILMATTO_MANAGER_RUNTIME ?? path.join(localAppData, "IlMatto", "manager-runtime"));
  const memoryRoot = path.resolve(runtime?.memoryRoot ?? process.env.ILMATTO_COMPANION_MEMORY_DIR ?? path.join(localAppData, "IlMatto", "companion-memory"));
  const managedAttachmentsRoot = path.join(localAppData, "IlMatto", "manager-sessions", "attachments");
  const runtimeAttachmentsRoot = path.resolve(runtime?.attachmentsRoot ?? path.join(configuredRuntime, "attachments"));
  const targets = [
    path.join(memoryRoot, "sessions", safeId),
    path.join(managedAttachmentsRoot, safeId),
    path.join(runtimeAttachmentsRoot, safeId),
  ];
  const failures: string[] = [];
  for (const target of targets) {
    try { await removeManagerSessionTargetWithRetry(target); }
    catch (error) { failures.push(`${target}: ${error instanceof Error ? error.message : "未知错误"}`); }
  }
  // Keep the helper best-effort across all owned locations, but report a
  // failure after attempting every target so the desktop can surface it.
  if (failures.length > 0) throw new Error(`部分 Manager 会话数据未能删除：\n${failures.join("\n")}`);
}

/** Windows may briefly keep an attachment open while the UI or an executor
 * finishes reading it. Retry only transient removal failures; never broaden
 * the target beyond the already validated session directory. */
async function removeManagerSessionTargetWithRetry(target: string): Promise<void> {
  const delays = [0, 120, 250, 500, 1000, 1500];
  let lastError: unknown;
  for (const delay of delays) {
    if (delay > 0) await new Promise((resolve) => setTimeout(resolve, delay));
    try {
      await rm(target, { recursive: true, force: true });
      return;
    } catch (error) {
      lastError = error;
    }
  }
  throw lastError instanceof Error ? lastError : new Error("删除会话目录失败。");
}

async function cleanupMcp(mount: ManagerMcpMount): Promise<void> {
  await withMcpConfigLock(() => cleanupMcpUnlocked(mount));
}

async function cleanupMcpUnlocked(mount: ManagerMcpMount): Promise<void> {
  try {
    const parsed = JSON.parse(await readFile(mount.configPath, "utf8"));
    if (!parsed || typeof parsed !== "object" || Array.isArray(parsed)) return;
    const config = parsed as Record<string, unknown>;
    const servers = config.mcpServers;
    if (!servers || typeof servers !== "object" || Array.isArray(servers)) return;
    const current = (servers as Record<string, unknown>)[mount.serverName];
    if (!deepEqual(current, mount.definition)) return;
    const remaining = { ...(servers as Record<string, unknown>) };
    delete remaining[mount.serverName];
    if (Object.keys(remaining).length === 0 && Object.keys(config).every((key) => key === "mcpServers")) {
      await rm(mount.configPath, { force: true });
    } else {
      await writeFile(mount.configPath, JSON.stringify({ ...config, mcpServers: remaining }, null, 2), "utf8");
    }
    if (mount.scope === "workspace-plugin" && mount.manifestPath && mount.manifest && mount.pluginDirectory) {
      try {
        const currentManifest = JSON.parse(await readFile(mount.manifestPath, "utf8"));
        if (deepEqual(currentManifest, mount.manifest)) {
          await rm(mount.manifestPath, { force: true });
          const entries = await readdir(mount.pluginDirectory);
          if (entries.length === 0) await rm(mount.pluginDirectory, { recursive: true, force: true });
        }
      } catch { }
    }
    if (mount.pluginRegistryPath && mount.pluginRegistryEntry && mount.pluginRegistryEntryCreated) {
      try {
        const parsed = JSON.parse(await readFile(mount.pluginRegistryPath, "utf8"));
        if (!parsed || typeof parsed !== "object" || Array.isArray(parsed)) return;
        const registry = parsed as Record<string, unknown>;
        const entries = registry.entries;
        if (!Array.isArray(entries)) return;
        const remaining = entries.filter((item) => !deepEqual(item, mount.pluginRegistryEntry));
        if (remaining.length === entries.length) return;
        if (remaining.length === 0 && Object.keys(registry).every((key) => key === "entries")) {
          await rm(mount.pluginRegistryPath, { force: true });
        } else {
          await writeFile(mount.pluginRegistryPath, JSON.stringify({ ...registry, entries: remaining }, null, 2), "utf8");
        }
      } catch { }
    }
  } catch {
    // Cleanup is best effort; never turn a completed Manager session into an
    // error because a user-owned workspace config became unavailable.
  }
}

function deepEqual(left: unknown, right: unknown): boolean {
  if (left === right) return true;
  if (!left || !right || typeof left !== "object" || typeof right !== "object") return false;
  if (Array.isArray(left) || Array.isArray(right)) {
    return Array.isArray(left) && Array.isArray(right) && left.length === right.length && left.every((value, index) => deepEqual(value, right[index]));
  }
  const leftKeys = Object.keys(left as Record<string, unknown>).sort();
  const rightKeys = Object.keys(right as Record<string, unknown>).sort();
  return leftKeys.length === rightKeys.length && leftKeys.every((key, index) => key === rightKeys[index] && deepEqual((left as any)[key], (right as any)[key]));
}
