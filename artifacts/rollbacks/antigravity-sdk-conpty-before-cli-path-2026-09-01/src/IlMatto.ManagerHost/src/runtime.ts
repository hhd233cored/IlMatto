import os from "node:os";
import path from "node:path";
import { createHash } from "node:crypto";
import { mkdir, rm, writeFile } from "node:fs/promises";
import { buildCompanionSystemPrompt, defaultCompanionProfile } from "./companion.js";
import type { CompanionProfile } from "./protocol.js";

export type ManagerRuntime = {
  root: string;
  schemaPath: string;
  logPath: string;
  /** A per-runtime name prevents collisions with the user's own AGY agents. */
  agentName: string;
  /** The generated global agent definition used by AGY CLI discovery. */
  agentPath: string;
};

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

/** Kept as the default prompt export for existing coordinator tests/callers. */
export const managerPolicyPrompt = buildCompanionSystemPrompt(defaultCompanionProfile);

function renderManagerAgent(agentName: string, systemPrompt: string): string {
  return `---
name: ${agentName}
description: IlMatto policy-limited general assistant and coding-task router.
tools: []
mainAgent: true
subagent: false
commandExecutionPolicy: off
skills: []
plugins: []
---

# IlMatto Manager Policy

${systemPrompt}
`;
}

export async function ensureManagerRuntime(profile?: CompanionProfile): Promise<ManagerRuntime> {
  const localAppData = process.env.LOCALAPPDATA ?? path.join(os.homedir(), "AppData", "Local");
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
  // The CLI documentation permits both `.agents/agents/<name>.md` and
  // `.agents/agents/<name>/agent.md`.  Older AGY builds only discover the
  // flat form, however, and silently fall back to the default agent when the
  // nested form is used.  Keep one canonical flat definition so the selected
  // agent (and its empty tool allow-list) is always loaded.
  const localAgentPath = path.join(agentsRoot, `${agentName}.md`);
  const nestedAgentDirectory = path.join(agentsRoot, agentName);
  const agentPath = path.join(globalAgentsRoot, `${agentName}.md`);
  const logPath = path.join(logDirectory, "antigravity-manager.log");
  await mkdir(projectSettingsDirectory, { recursive: true });
  // Keep terminal rendering and policy settings inside the isolated manager
  // workspace. This prevents ConPTY redraws while leaving the user's global
  // AGY preferences and credentials untouched.
  await writeFile(projectSettingsPath, JSON.stringify({
    altScreenMode: "never",
    verbosity: "low",
    enableTerminalSandbox: true,
    allowNonWorkspaceAccess: false,
    toolPermission: "strict",
  }, null, 2), "utf8");
  await writeFile(schemaPath, JSON.stringify(managerActionSchema, null, 2), "utf8");
  const agentDefinition = renderManagerAgent(agentName, buildCompanionSystemPrompt(profile));
  // Keep a workspace-local copy for diagnostics and for AGY versions that
  // discover `.agents` without consulting the global customization folder.
  await writeFile(localAgentPath, agentDefinition, "utf8");
  // The CLI's global customization directory is the reliable discovery path
  // for a workspace which intentionally is not a Git repository (our isolated
  // manager runtime).  The name is fingerprinted, so we never overwrite a
  // user-created agent with the same human-readable name.
  await writeFile(agentPath, agentDefinition, "utf8");
  // Remove the previously generated nested definition.  Leaving two files
  // with the same agent name can make resolver behaviour version-dependent.
  await rm(nestedAgentDirectory, { recursive: true, force: true });
  return { root, schemaPath, logPath, agentName, agentPath };
}

/** Remove only the generated files; stale files are harmless but cleanup keeps
 * the user's global AGY customization directory tidy after shutdown. */
export async function cleanupManagerRuntime(runtime: ManagerRuntime | undefined): Promise<void> {
  if (!runtime) return;
  try { await rm(runtime.agentPath, { force: true }); } catch { }
  try { await rm(path.join(runtime.root, ".agents", "agents", `${runtime.agentName}.md`), { force: true }); } catch { }
}
