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
  createConversation,
  getConversation,
  listConversations,
  sendMessage,
  submitReviewDecision,
  uploadFiles,
} from "./api";

const samplePrompts = [
  "Create a capital call notice for Apex Fund I for INR 1000",
  "Generate a one-pager for BlueWave Systems",
  "What is the status of onboarding?",
  "Approve the capital call allocation review",
];

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

type CapitalCallReviewNotice = {
  rootFundName?: string;
  rootCurrency?: string;
  rootCapitalCallAmount?: number;
  fundBreakdowns?: Array<unknown>;
  leafAllocations?: Array<{
    investorName?: string;
    currency?: string;
    contributionAmount?: number;
  }>;
};

function formatMoney(value?: number, currency?: string) {
  if (value == null) {
    return "Pending";
  }

  const formatted = value.toLocaleString(undefined, {
    minimumFractionDigits: 2,
    maximumFractionDigits: 2,
  });

  return currency ? `${currency} ${formatted}` : formatted;
}

function parseCapitalCallReviewNotice(rawJson: string): CapitalCallReviewNotice | null {
  try {
    const parsed = JSON.parse(rawJson) as { notice?: CapitalCallReviewNotice };
    return parsed.notice ?? null;
  } catch {
    return null;
  }
}

export default function App() {
  const [conversationId, setConversationId] = useState<string | null>(() => {
    return localStorage.getItem("conversational-orchestration-conversation");
  });
  const [snapshot, setSnapshot] = useState<ConversationSnapshot | null>(null);
  const [conversationHistory, setConversationHistory] = useState<ConversationSummary[]>([]);
  const [message, setMessage] = useState("");
  const [uploadQueue, setUploadQueue] = useState<FileAsset[]>([]);
  const [isFilesOpen, setIsFilesOpen] = useState(true);
  const [historyError, setHistoryError] = useState<string | null>(null);
  const [isBusy, setIsBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const fileInputRef = useRef<HTMLInputElement | null>(null);

  useEffect(() => {
    if (conversationId) {
      localStorage.setItem("conversational-orchestration-conversation", conversationId);
    } else {
      localStorage.removeItem("conversational-orchestration-conversation");
    }
  }, [conversationId]);

  useEffect(() => {
    let isMounted = true;

    const refresh = async () => {
      const [conversationResult, historyResult] = await Promise.allSettled([
        conversationId ? getConversation(conversationId) : Promise.resolve(null),
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
        setHistoryError(null);
      } else {
        setHistoryError("Unable to load previous threads. Restart the API if it is still running from an older build.");
      }
    };

    refresh();
    const timer = window.setInterval(refresh, 4000);
    return () => {
      isMounted = false;
      window.clearInterval(timer);
    };
  }, [conversationId]);

  async function refreshConversationHistory() {
    try {
      setConversationHistory(await listConversations());
      setHistoryError(null);
    } catch {
      setHistoryError("Unable to load previous threads. Restart the API if it is still running from an older build.");
    }
  }

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
      await refreshConversationHistory();
    } catch (sendError) {
      setError(sendError instanceof Error ? sendError.message : "Unable to send message");
    } finally {
      setIsBusy(false);
    }
  }

  async function handleFilesSelected(files: FileList | null) {
    if (!files || files.length === 0) return;

    setIsBusy(true);
    setError(null);

    try {
      let workingConversationId = conversationId;
      if (!workingConversationId) {
        const conversation = await createConversation();
        workingConversationId = conversation.conversationId;
        setConversationId(workingConversationId);
        setSnapshot(conversation);
        await refreshConversationHistory();
      }

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
    localStorage.removeItem("conversational-orchestration-conversation");
    setConversationId(null);
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
            <div className="stack thread-list">
              {historyError ? (
                <p className="history-warning">{historyError}</p>
              ) : conversationHistory.length ? (
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
            <span className="conversation-id">{conversationId ? `#${conversationId.slice(0, 8)}` : "new"}</span>
          </div>

          <div className="message-stream">
            {snapshot?.messages.length ? (
              snapshot.messages.map((entry) => (
                <MessageCard key={entry.id} message={entry} operations={snapshot.operations} />
              ))
            ) : (
              <div className="empty-state">
                <p>No messages yet.</p>
                <span>Try creating a capital call notice, then approve the allocation review to generate the Excel output.</span>
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
                <p className="muted">Capital call allocation reviews and onboarding checkpoints will appear here whenever a workflow pauses for approval.</p>
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
                  No running work yet. Start a capital call or onboarding flow to see active operations and review checkpoints show up side by side.
                </p>
              ) : null}
            </div>
          </section>

          <section className="panel-section">
            <button
              className="accordion-heading"
              type="button"
              aria-expanded={isFilesOpen}
              onClick={() => setIsFilesOpen((current) => !current)}
            >
              <span>
                <strong>Uploaded Files</strong>
                <small>{(snapshot?.files.length ?? 0) + uploadQueue.length} attached</small>
              </span>
              <span className={`accordion-chevron ${isFilesOpen ? "open" : ""}`}>⌄</span>
            </button>
            {isFilesOpen ? (
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
            ) : null}
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
  const capitalCallNotice = task.taskType === "CapitalCallAllocationReview"
    ? parseCapitalCallReviewNotice(task.proposedPayloadJson)
    : null;
  const approveLabel = task.taskType === "CapitalCallAllocationReview"
    ? "Approve & Generate Excel"
    : "Approve";

  return (
    <article className="review-card">
      <div className="card-topline">
        <strong>{task.title}</strong>
        <span>{task.taskType}</span>
      </div>
      <p>{operation?.title ?? "Unknown operation"}</p>
      {capitalCallNotice ? (
        <div className="review-preview">
          <div className="review-preview-header">
            <strong>{capitalCallNotice.rootFundName ?? "Capital Call Allocations"}</strong>
            <span>{formatMoney(capitalCallNotice.rootCapitalCallAmount, capitalCallNotice.rootCurrency)}</span>
          </div>
          <div className="review-preview-meta">
            <span>{capitalCallNotice.fundBreakdowns?.length ?? 0} fund rollups</span>
            <span>{capitalCallNotice.leafAllocations?.length ?? 0} leaf allocations</span>
          </div>
          {(capitalCallNotice.leafAllocations ?? []).slice(0, 4).map((allocation, index) => (
            <div className="review-preview-row" key={`${allocation.investorName ?? "investor"}-${index}`}>
              <span>{allocation.investorName ?? "Investor"}</span>
              <strong>{formatMoney(allocation.contributionAmount, allocation.currency)}</strong>
            </div>
          ))}
        </div>
      ) : null}
      <div className="review-actions">
        <button className="secondary-button" type="button" onClick={onReject}>
          Reject
        </button>
        <button className="primary-button review-approve-button" type="button" onClick={onApprove}>
          {approveLabel}
        </button>
      </div>
      <pre>{task.proposedPayloadJson}</pre>
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
                  if (action.route.startsWith("/api/")) {
                    const baseUrl = import.meta.env.VITE_API_BASE_URL ?? "http://localhost:8080";
                    window.open(new URL(action.route, baseUrl).toString(), "_blank", "noopener,noreferrer");
                    return;
                  }

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
