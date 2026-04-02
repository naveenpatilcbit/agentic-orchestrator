export type AgentAction = {
  type: string;
  label: string;
  route?: string | null;
  payloadJson?: string | null;
};

export type ConversationMessage = {
  id: string;
  conversationId: string;
  operationId?: string | null;
  role: string;
  content: string;
  messageKind: string;
  actions: AgentAction[];
  createdAtUtc: string;
};

export type AgentOperation = {
  id: string;
  agentId: string;
  title: string;
  status: string;
  currentStep: string;
  summary: string;
  pendingClarification?: string | null;
  activeReviewTaskId?: string | null;
  updatedAtUtc: string;
};

export type ReviewTask = {
  id: string;
  operationId: string;
  title: string;
  taskType: string;
  status: string;
  proposedPayloadJson: string;
  finalPayloadJson?: string | null;
  notes?: string | null;
  updatedAtUtc: string;
};

export type FileAsset = {
  id: string;
  conversationId: string;
  fileName: string;
  contentType: string;
  sizeBytes: number;
  uploadedAtUtc: string;
};

export type ConversationSnapshot = {
  conversationId: string;
  title: string;
  messages: ConversationMessage[];
  operations: AgentOperation[];
  reviewTasks: ReviewTask[];
  files: FileAsset[];
};

export type ConversationSummary = {
  conversationId: string;
  title: string;
  messageCount: number;
  activeOperationCount: number;
  openReviewCount: number;
  updatedAtUtc: string;
};
