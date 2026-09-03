import type { CodingAgentConfig, CodingProvider, ManagerImageAttachment } from "./protocol.js";

export interface CodingWorker {
  readonly provider: CodingProvider;
  readonly sessionRef?: string;
  start(): Promise<void>;
  sendCodeTask(taskId: string, userRequest: string, attachments?: ManagerImageAttachment[]): void;
  resolve(requestId: string, approved: boolean, values?: Record<string, unknown>): void;
  cancel(): void;
  deleteSession?(): Promise<void> | void;
  dispose(): Promise<void>;
}

export type CodingWorkerEvent = Record<string, any> & { type: string; sessionId?: string };
export type CodingWorkerFactoryConfig = { sessionId: string; workspacePath: string; config: CodingAgentConfig };
