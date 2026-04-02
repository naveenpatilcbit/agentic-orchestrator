import type { ConversationSnapshot, ConversationSummary, FileAsset } from "./types";

const API_BASE = import.meta.env.VITE_API_BASE_URL ?? "";

const defaultHeaders: HeadersInit = {
  "Content-Type": "application/json",
  "X-Tenant-Id": "tenant-demo",
  "X-User-Id": "user-demo",
  "X-User-Name": "Fund Admin Demo",
};

async function parseResponse<T>(response: Response): Promise<T> {
  if (!response.ok) {
    const text = await response.text();
    throw new Error(text || `Request failed with ${response.status}`);
  }

  return response.json() as Promise<T>;
}

export async function sendMessage(payload: {
  conversationId?: string;
  message: string;
  attachmentIds?: string[];
}): Promise<ConversationSnapshot> {
  const response = await fetch(`${API_BASE}/api/chat/messages`, {
    method: "POST",
    headers: defaultHeaders,
    body: JSON.stringify({
      conversationId: payload.conversationId,
      message: payload.message,
      attachmentIds: payload.attachmentIds ?? [],
    }),
  });

  return parseResponse<ConversationSnapshot>(response);
}

export async function getConversation(
  conversationId: string,
): Promise<ConversationSnapshot> {
  const response = await fetch(`${API_BASE}/api/chat/conversations/${conversationId}`, {
    headers: defaultHeaders,
  });

  return parseResponse<ConversationSnapshot>(response);
}

export async function listConversations(): Promise<ConversationSummary[]> {
  const response = await fetch(`${API_BASE}/api/chat/conversations`, {
    headers: defaultHeaders,
  });

  return parseResponse<ConversationSummary[]>(response);
}

export async function submitReviewDecision(
  reviewTaskId: string,
  decision: "Approved" | "Rejected",
  finalPayloadJson: string,
): Promise<ConversationSnapshot> {
  const response = await fetch(`${API_BASE}/api/reviews/${reviewTaskId}/decision`, {
    method: "POST",
    headers: defaultHeaders,
    body: JSON.stringify({
      decision,
      finalPayloadJson,
      notes: `Submitted from sample UI as ${decision}`,
    }),
  });

  return parseResponse<ConversationSnapshot>(response);
}

export async function uploadFiles(
  conversationId: string,
  files: File[],
): Promise<FileAsset[]> {
  const formData = new FormData();
  formData.append("conversationId", conversationId);
  files.forEach((file) => formData.append("files", file));

  const response = await fetch(`${API_BASE}/api/files/upload`, {
    method: "POST",
    headers: {
      "X-Tenant-Id": "tenant-demo",
      "X-User-Id": "user-demo",
      "X-User-Name": "Fund Admin Demo",
    },
    body: formData,
  });

  return parseResponse<FileAsset[]>(response);
}
