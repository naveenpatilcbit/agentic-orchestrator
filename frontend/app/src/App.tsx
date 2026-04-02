import { useEffect, useMemo, useRef, useState } from "react";
import "./App.css";
import type {
  AgentAction,
  AgentOperation,
  ConversationMessage,
  ConversationSnapshot,
  ConversationSummary,
  FileAsset,
  ReviewTask,
} from "./types";
import {
  getConversation,
  listConversations,
  sendMessage,
  submitReviewDecision,
  uploadFiles,
} from "./api";

const samplePrompts = [
  "Create a capital call notice for Apex Fund I for $3,500,000",
  "Generate a one-pager for BlueWave Systems",
  "What is the status of onboarding?",
  "Approve the extraction review",
];

function createConversationId() {
  return crypto.randomUUID().replace(/-/g, "");
}

function formatTime(value: string) {
  return new Date(value).toLocaleTimeString([], {
    hour: "2-digit",
    minute: "2-digit",
  });
}

function formatThreadTime(value: string) {
  return new Date(value).toLocaleString([], {
    month: "short",
    day: "numeric",
    hour: "2-digit",
    minute: "2-digit",
  });
}

function formatBytes(value: number) {
  if (value < 1024) return `${value} B`;
  if (value < 1024 * 1024) return `${(value / 1024).toFixed(1)} KB`;
  return `${(value / (1024 * 1024)).toFixed(1)} MB`;
}

function actionTone(type: string) {
  switch (type) {
    case "OpenPageWithPrefill":
      return "success";
    case "DownloadArtifact":
      return "accent";
    case "AskForMoreInfo":
      return "warning";
    default:
      return "neutral";
  }
}

export default function App() {
  const [conversationId, setConversationId] = useState<string>(() => {
    return localStorage.getItem("fund-orchestrator-conversation") ?? createConversationId();
  });
  const [snapshot, setSnapshot] = useState<ConversationSnapshot | null>(null);
  const [conversationHistory, setConversationHistory] = useState<ConversationSummary[]>([]);
  const [message, setMessage] = useState("");
  const [uploadQueue, setUploadQueue] = useState<FileAsset[]>([]);
  const [isBusy, setIsBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const fileInputRef = useRef<HTMLInputElement | null>(null);

  useEffect(() => {
    localStorage.setItem("fund-orchestrator-conversation", conversationId);
  }, [conversationId]);

  useEffect(() => {
    let isMounted = true;

    const refresh = async () => {
      const [conversationResult, historyResult] = await Promise.allSettled([
        getConversation(conversationId),
        listConversations(),
      ]);

      if (!isMounted) {
        return;
      }

      if (conversationResult.status === "fulfilled") {
        setSnapshot(conversationResult.value);
      } else {
        setSnapshot(null);
      }

      if (historyResult.status === "fulfilled") {
        setConversationHistory(historyResult.value);
      }
    };

    refresh();
    const timer = window.setInterval(refresh, 4000);
    return () => {
      isMounted = false;
      window.clearInterval(timer);
    };
  }, [conversationId]);

  const activeOperations = useMemo(
    () =>
      snapshot?.operations.filter(
        (operation) =>
          !["Completed", "Failed", "Cancelled"].includes(operation.status),
      ) ?? [],
    [snapshot],
  );

  const openReviewTasks = useMemo(
    () => snapshot?.reviewTasks.filter((task) => task.status === "Open") ?? [],
    [snapshot],
  );

  async function handleSend(inputMessage = message) {
    if (!inputMessage.trim()) return;

    setIsBusy(true);
    setError(null);

    try {
      const data = await sendMessage({
        conversationId,
        message: inputMessage,
        attachmentIds: uploadQueue.map((file) => file.id),
      });

      setSnapshot(data);
      setConversationId(data.conversationId);
      setMessage("");
      setUploadQueue([]);
    } catch (sendError) {
      setError(sendError instanceof Error ? sendError.message : "Unable to send message");
    } finally {
      setIsBusy(false);
    }
  }

  async function handleFilesSelected(files: FileList | null) {
    if (!files || files.length === 0) return;

    const workingConversationId = conversationId || createConversationId();
    setConversationId(workingConversationId);
    setIsBusy(true);
    setError(null);

    try {
      const stored = await uploadFiles(workingConversationId, Array.from(files));
      setUploadQueue((current) => [...current, ...stored]);
    } catch (uploadError) {
      setError(uploadError instanceof Error ? uploadError.message : "Upload failed");
    } finally {
      setIsBusy(false);
      if (fileInputRef.current) {
        fileInputRef.current.value = "";
      }
    }
  }

  async function handleDecision(task: ReviewTask, decision: "Approved" | "Rejected") {
    setIsBusy(true);
    setError(null);
    try {
      const updated = await submitReviewDecision(
        task.id,
        decision,
        task.proposedPayloadJson,
      );
      setSnapshot(updated);
    } catch (decisionError) {
      setError(
        decisionError instanceof Error ? decisionError.message : "Unable to submit review decision",
      );
    } finally {
      setIsBusy(false);
    }
  }

  function handleOpenConversation(nextConversationId: string) {
    if (nextConversationId === conversationId) {
      return;
    }

    setConversationId(nextConversationId);
    setSnapshot(null);
    setUploadQueue([]);
    setMessage("");
    setError(null);
  }

  function resetConversation() {
    const nextConversationId = createConversationId();
    localStorage.setItem("fund-orchestrator-conversation", nextConversationId);
    setConversationId(nextConversationId);
    setSnapshot(null);
    setMessage("");
    setUploadQueue([]);
    setError(null);
  }

  return (
    <div className="shell">
      <header className="hero">
        <div>
          <p className="eyebrow">Private Equity AI Operating Layer</p>
          <h1>Fund Orchestrator Command Center</h1>
        </div>

        <div className="hero-metrics">
          <MetricCard label="Conversation" value={snapshot?.title ?? "Fresh thread"} />
          <MetricCard label="Active work" value={String(activeOperations.length)} />
          <MetricCard label="Open reviews" value={String(openReviewTasks.length)} />
        </div>
      </header>

      <main className="workspace">
        <aside className="panel side-panel">
          <section className="panel-section">
            <div className="panel-heading">
              <h2>Prompt Deck</h2>
              <button className="ghost-button" onClick={resetConversation}>
                New thread
              </button>
            </div>
            <div className="prompt-grid">
              {samplePrompts.map((prompt) => (
                <button
                  key={prompt}
                  className="prompt-chip"
                  onClick={() => setMessage(prompt)}
                >
                  {prompt}
                </button>
              ))}
            </div>
          </section>

          <section className="panel-section">
            <div className="panel-heading">
              <h2>Previous Threads</h2>
              <span>{conversationHistory.length}</span>
            </div>
            <div className="stack">
              {conversationHistory.length ? (
                conversationHistory.map((thread) => (
                  <button
                    key={thread.conversationId}
                    type="button"
                    className={`thread-card ${thread.conversationId === conversationId ? "active" : ""}`}
                    onClick={() => handleOpenConversation(thread.conversationId)}
                  >
                    <div className="thread-card-topline">
                      <strong>{thread.title}</strong>
                      {thread.conversationId === conversationId ? <span className="thread-badge">Live</span> : null}
                    </div>
                    <div className="thread-card-meta">
                      <span>{thread.messageCount} msgs</span>
                      <span>{thread.activeOperationCount} active</span>
                      <span>{thread.openReviewCount} reviews</span>
                    </div>
                    <span className="thread-card-time">{formatThreadTime(thread.updatedAtUtc)}</span>
                  </button>
                ))
              ) : (
                <p className="muted">Previous threads will appear here after the first saved conversation.</p>
              )}
            </div>
          </section>

          <section className="panel-section">
            <div className="panel-heading">
              <h2>Uploaded Files</h2>
              <span>{(snapshot?.files.length ?? 0) + uploadQueue.length}</span>
            </div>
            <div className="file-stack">
              {uploadQueue.map((file) => (
                <FilePill key={file.id} file={file} pending />
              ))}
              {snapshot?.files.map((file) => (
                <FilePill key={file.id} file={file} />
              ))}
              {snapshot?.files.length === 0 && uploadQueue.length === 0 ? (
                <p className="muted">Upload agreements, subscription docs, or templates before you ask.</p>
              ) : null}
            </div>
          </section>

          <section className="panel-section">
            <div className="panel-heading">
              <h2>Active Work</h2>
              <span>{activeOperations.length}</span>
            </div>
            <div className="stack">
              {activeOperations.map((operation) => (
                <OperationCard key={operation.id} operation={operation} />
              ))}
              {activeOperations.length === 0 ? (
                <p className="muted">
                  No running work yet. Start onboarding, then ask for a one-pager in the same thread
                  to see concurrent operations handled side by side.
                </p>
              ) : null}
            </div>
          </section>
        </aside>

        <section className="panel conversation-panel">
          <div className="panel-heading">
            <div>
              <h2>Conversation Thread</h2>
              <p className="subtle">
                Same chat, many operations. Routing will continue an existing workflow only when the
                message is clearly attached to it.
              </p>
            </div>
            <span className="conversation-id">#{conversationId.slice(0, 8)}</span>
          </div>

          <div className="message-stream">
            {snapshot?.messages.length ? (
              snapshot.messages.map((entry) => (
                <MessageCard key={entry.id} message={entry} operations={snapshot.operations} />
              ))
            ) : (
              <div className="empty-state">
                <p>No messages yet.</p>
                <span>Try uploading onboarding docs, then ask for another agent while the workflow waits.</span>
              </div>
            )}
          </div>

          <div className="composer">
            <textarea
              value={message}
              onChange={(event) => setMessage(event.target.value)}
              placeholder="Ask for notice creation, onboarding, status, or a one-pager..."
              rows={4}
            />

            <div className="composer-footer">
              <div className="composer-actions">
                <input
                  ref={fileInputRef}
                  type="file"
                  multiple
                  hidden
                  onChange={(event) => handleFilesSelected(event.target.files)}
                />
                <button
                  className="ghost-button"
                  onClick={() => fileInputRef.current?.click()}
                  type="button"
                >
                  Attach files
                </button>
                <span className="hint">Uploads stay attached to this conversation.</span>
              </div>

              <button className="primary-button" onClick={() => handleSend()} disabled={isBusy}>
                {isBusy ? "Working..." : "Send"}
              </button>
            </div>
          </div>

          {error ? <div className="error-banner">{error}</div> : null}
        </section>

        <aside className="panel review-panel">
          <section className="panel-section">
            <div className="panel-heading">
              <h2>Review Queue</h2>
              <span>{openReviewTasks.length}</span>
            </div>
            <div className="stack">
              {openReviewTasks.map((task) => (
                <ReviewCard
                  key={task.id}
                  task={task}
                  operations={snapshot?.operations ?? []}
                  onApprove={() => handleDecision(task, "Approved")}
                  onReject={() => handleDecision(task, "Rejected")}
                />
              ))}
              {openReviewTasks.length === 0 ? (
                <p className="muted">When the onboarding saga pauses for classification or extraction review, it will show up here.</p>
              ) : null}
            </div>
          </section>

          <section className="panel-section audit-note">
            <h2>Routing Rule</h2>
            <p>
              If the thread contains an active onboarding flow and you ask for a one-pager, the system
              starts a second operation instead of hijacking the first. Approval language only auto-routes
              when there is a single open review task.
            </p>
          </section>
        </aside>
      </main>
    </div>
  );
}

function MetricCard({ label, value }: { label: string; value: string }) {
  return (
    <div className="metric-card">
      <span>{label}</span>
      <strong>{value}</strong>
    </div>
  );
}

function FilePill({ file, pending = false }: { file: FileAsset; pending?: boolean }) {
  return (
    <div className={`file-pill ${pending ? "pending" : ""}`}>
      <div>
        <strong>{file.fileName}</strong>
        <span>{formatBytes(file.sizeBytes)}</span>
      </div>
      <em>{pending ? "queued for send" : "uploaded"}</em>
    </div>
  );
}

function OperationCard({ operation }: { operation: AgentOperation }) {
  return (
    <article className="operation-card">
      <div className="card-topline">
        <span className={`status-dot status-${operation.status.toLowerCase()}`} />
        <strong>{operation.title}</strong>
      </div>
      <p>{operation.summary}</p>
      <div className="meta-row">
        <span>{operation.status}</span>
        <span>{operation.currentStep}</span>
      </div>
      {operation.pendingClarification ? <small>{operation.pendingClarification}</small> : null}
    </article>
  );
}

function ReviewCard({
  task,
  operations,
  onApprove,
  onReject,
}: {
  task: ReviewTask;
  operations: AgentOperation[];
  onApprove: () => void;
  onReject: () => void;
}) {
  const operation = operations.find((candidate) => candidate.id === task.operationId);

  return (
    <article className="review-card">
      <div className="card-topline">
        <strong>{task.title}</strong>
        <span>{task.taskType}</span>
      </div>
      <p>{operation?.title ?? "Unknown operation"}</p>
      <pre>{task.proposedPayloadJson}</pre>
      <div className="review-actions">
        <button className="secondary-button" onClick={onReject}>
          Reject
        </button>
        <button className="primary-button" onClick={onApprove}>
          Approve
        </button>
      </div>
    </article>
  );
}

function MessageCard({
  message,
  operations,
}: {
  message: ConversationMessage;
  operations: AgentOperation[];
}) {
  const operation = operations.find((candidate) => candidate.id === message.operationId);

  return (
    <article className={`message-card role-${message.role.toLowerCase()}`}>
      <div className="message-meta">
        <span className="message-role">{message.role}</span>
        {operation ? <span className="operation-chip">{operation.title}</span> : null}
        <span>{formatTime(message.createdAtUtc)}</span>
      </div>
      <p>{message.content}</p>
      {message.actions.length ? (
        <div className="message-actions">
          {message.actions.map((action: AgentAction) => (
            <button
              key={`${message.id}-${action.label}`}
              type="button"
              className={`action-chip tone-${actionTone(action.type)}`}
              onClick={() => {
                if (action.route) {
                  window.alert(`Open route: ${action.route}`);
                }
              }}
            >
              {action.label}
            </button>
          ))}
        </div>
      ) : null}
    </article>
  );
}
