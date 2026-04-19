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
  "Open the capital call review task",
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

function formatCurrencyAmount(amount?: number, currency?: string) {
  if (amount === undefined || amount === null) {
    return currency ? `${currency} pending` : "Pending";
  }

  const formatted = amount.toLocaleString([], {
    minimumFractionDigits: 2,
    maximumFractionDigits: 2,
  });

  return currency ? `${currency} ${formatted}` : formatted;
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

type CapitalCallReviewPartner = {
  partnerName?: string;
  partnerType?: string;
  commitmentPercentage?: number;
  childFund?: CapitalCallReviewFund | null;
};

type CapitalCallReviewFund = {
  fundName?: string;
  fundCurrency?: string;
  path?: string;
  partners?: CapitalCallReviewPartner[];
};

type CapitalCallReviewPayload = {
  reviewDownloadRoute?: string;
  reviewFileName?: string;
  rootFund?: CapitalCallReviewFund;
};

type CapitalCallFundBreakdown = {
  fundName?: string;
  fundCurrency?: string;
  amountToRaise?: number;
  path?: string;
};

type CapitalCallLeafAllocation = {
  investorName?: string;
  currency?: string;
  contributionAmount?: number;
  parentFundPath?: string;
};

type CapitalCallNoticePayload = {
  rootFundName?: string;
  rootCurrency?: string;
  rootCapitalCallAmount?: number;
  fundBreakdowns?: CapitalCallFundBreakdown[];
  leafAllocations?: CapitalCallLeafAllocation[];
};

type CapitalCallConfirmationPayload = {
  reviewedExtraction?: CapitalCallReviewPayload;
  notice?: CapitalCallNoticePayload;
};

function parseCapitalCallReviewPayload(rawJson: string): CapitalCallReviewPayload | null {
  try {
    return JSON.parse(rawJson) as CapitalCallReviewPayload;
  } catch {
    return null;
  }
}

function parseCapitalCallConfirmationPayload(rawJson: string): CapitalCallConfirmationPayload | null {
  try {
    return JSON.parse(rawJson) as CapitalCallConfirmationPayload;
  } catch {
    return null;
  }
}

function prettyJson(rawJson: string) {
  try {
    return JSON.stringify(JSON.parse(rawJson), null, 2);
  } catch {
    return rawJson;
  }
}

function normalizeJson(rawJson: string) {
  try {
    return JSON.stringify(JSON.parse(rawJson));
  } catch {
    return null;
  }
}

function countFundNodes(fund?: CapitalCallReviewFund | null): number {
  if (!fund) {
    return 0;
  }

  return 1 + (fund.partners ?? []).reduce(
    (total, partner) => total + countFundNodes(partner.childFund),
    0,
  );
}

function countPartners(fund?: CapitalCallReviewFund | null): number {
  if (!fund) {
    return 0;
  }

  return (fund.partners?.length ?? 0) + (fund.partners ?? []).reduce(
    (total, partner) => total + countPartners(partner.childFund),
    0,
  );
}

type CapitalCallReviewPreviewRow = {
  path: string;
  partnerName: string;
  partnerType: string;
  commitmentPercentage?: number;
};

function buildCapitalCallPreviewRows(
  fund?: CapitalCallReviewFund | null,
  rows: CapitalCallReviewPreviewRow[] = [],
): CapitalCallReviewPreviewRow[] {
  if (!fund) {
    return rows;
  }

  for (const partner of fund.partners ?? []) {
    rows.push({
      path: fund.path ?? fund.fundName ?? "Fund",
      partnerName: partner.partnerName ?? "Partner",
      partnerType: partner.partnerType ?? "Investor",
      commitmentPercentage: partner.commitmentPercentage,
    });

    if (partner.childFund) {
      buildCapitalCallPreviewRows(partner.childFund, rows);
    }
  }

  return rows;
}

function parseActionPayload(rawJson?: string | null): { startMessage?: string } | null {
  if (!rawJson) {
    return null;
  }

  try {
    return JSON.parse(rawJson) as { startMessage?: string };
  } catch {
    return null;
  }
}

function openFileDownload(fileId: string) {
  const baseUrl = import.meta.env.VITE_API_BASE_URL ?? "http://localhost:8080";
  window.open(new URL(`/api/files/${fileId}/download`, baseUrl).toString(), "_blank", "noopener,noreferrer");
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
      const [snapshotResult, historyResult] = await Promise.allSettled([
        conversationId ? getConversation(conversationId) : Promise.resolve(null),
        listConversations(),
      ]);

      if (!isMounted) {
        return;
      }

      if (snapshotResult.status === "fulfilled") {
        if (snapshotResult.value) {
          setSnapshot(snapshotResult.value);
        }
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

  const activeOperation = activeOperations[0] ?? null;

  const openReviewTasks = useMemo(
    () =>
      snapshot?.reviewTasks.filter(
        (task) =>
          task.status === "Open" &&
          (!activeOperation || task.operationId === activeOperation.id),
      ) ?? [],
    [activeOperation, snapshot],
  );

  const activeReviewTask = openReviewTasks[0] ?? null;

  const uploadedInputFiles = useMemo(
    () =>
      snapshot?.files.filter((file) => file.kind === "UploadedInput") ?? [],
    [snapshot],
  );

  const generatedArtifacts = useMemo(
    () =>
      snapshot?.files.filter((file) => file.kind === "GeneratedArtifact") ?? [],
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
        clientMessageId:
          typeof crypto !== "undefined" && "randomUUID" in crypto
            ? crypto.randomUUID()
            : `${Date.now()}-${Math.random().toString(16).slice(2)}`,
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

  async function handleDecision(
    task: ReviewTask,
    action: string,
    options?: {
      finalPayloadJson?: string;
      changeRequestText?: string;
      notes?: string;
    },
  ) {
    setIsBusy(true);
    setError(null);
    try {
      const updated = await submitReviewDecision(
        task.id,
        action,
        {
          ...options,
          clientRequestId:
            typeof crypto !== "undefined" && "randomUUID" in crypto
              ? crypto.randomUUID()
              : `${Date.now()}-${Math.random().toString(16).slice(2)}`,
        },
      );
      setSnapshot(updated);
      await refreshConversationHistory();
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

    void (async () => {
      setIsBusy(true);
      setError(null);
      try {
        const data = await getConversation(nextConversationId);
        setConversationId(data.conversationId);
        setSnapshot(data);
        setUploadQueue([]);
        setMessage("");
        await refreshConversationHistory();
      } catch (openError) {
        setError(openError instanceof Error ? openError.message : "Unable to open thread");
      } finally {
        setIsBusy(false);
      }
    })();
  }

  function resetConversation() {
    void (async () => {
      setIsBusy(true);
      setError(null);
      try {
        localStorage.removeItem("conversational-orchestration-conversation");
        setConversationId(null);
        setSnapshot(null);
        setMessage("");
        setUploadQueue([]);
        await refreshConversationHistory();
      } catch (resetError) {
        setError(resetError instanceof Error ? resetError.message : "Unable to clear active thread");
      } finally {
        setIsBusy(false);
      }
    })();
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
          <MetricCard label="Active work" value={String(activeOperation ? 1 : 0)} />
          <MetricCard label="Open reviews" value={String(activeReviewTask ? 1 : 0)} />
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
                    </div>
                    <div className="thread-card-meta">
                      <span>{thread.messageCount} msgs</span>
                      <span>{thread.activeOperationCount ? "workflow active" : "idle"}</span>
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
                One active workflow lives in a thread at a time. Start a new thread whenever you
                want separate work.
              </p>
            </div>
            <span className="conversation-id">{conversationId ? `#${conversationId.slice(0, 8)}` : "new"}</span>
          </div>

          <div className="message-stream">
            {snapshot?.messages.length ? (
              snapshot.messages.map((entry) => (
                <MessageCard
                  key={entry.id}
                  message={entry}
                  operations={snapshot.operations}
                  onAction={async (action) => {
                    if (action.type === "StartAsyncOperation") {
                      const payload = parseActionPayload(action.payloadJson);
                      if (payload?.startMessage) {
                        await handleSend(payload.startMessage);
                        return;
                      }
                    }

                    if (action.route) {
                      if (action.route.startsWith("/api/")) {
                        const baseUrl = import.meta.env.VITE_API_BASE_URL ?? "http://localhost:8080";
                        window.open(new URL(action.route, baseUrl).toString(), "_blank", "noopener,noreferrer");
                        return;
                      }

                      window.alert(`Open route: ${action.route}`);
                    }
                  }}
                />
              ))
            ) : (
              <div className="empty-state">
                <p>No messages yet.</p>
                <span>Try creating a capital call notice, review the extracted partner data, then start a template output operation from the approved result.</span>
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
              <h2>Current Review</h2>
              <span>{activeReviewTask ? 1 : 0}</span>
            </div>
            <div className="stack">
              {openReviewTasks.map((task) => (
                activeReviewTask && task.id === activeReviewTask.id ? (
                  <ReviewCard
                    key={task.id}
                    task={task}
                    operations={snapshot?.operations ?? []}
                    onSubmit={(action, options) => {
                      void handleDecision(task, action, options);
                    }}
                  />
                ) : null
              ))}
              {openReviewTasks.length === 0 ? (
                <p className="muted">The current workflow review step will appear here whenever the session pauses for human input.</p>
              ) : null}
            </div>
          </section>

          <section className="panel-section">
            <div className="panel-heading">
              <h2>Current Work</h2>
              <span>{activeOperation ? 1 : 0}</span>
            </div>
            <div className="stack">
              {activeOperation ? (
                <OperationCard operation={activeOperation} />
              ) : (
                <p className="muted">
                  No running work yet. Start a workflow in this thread to see its live status and review checkpoint here.
                </p>
              )}
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
                <strong>Conversation Files</strong>
                <small>{(snapshot?.files.length ?? 0) + uploadQueue.length} attached</small>
              </span>
              <span className={`accordion-chevron ${isFilesOpen ? "open" : ""}`}>⌄</span>
            </button>
            {isFilesOpen ? (
              <div className="file-stack">
                <div className="file-group">
                  <div className="file-group-heading">
                    <strong>Uploaded Inputs</strong>
                    <span>{uploadQueue.length + uploadedInputFiles.length}</span>
                  </div>
                  {uploadQueue.map((file) => (
                    <FilePill key={file.id} file={file} pending />
                  ))}
                  {uploadedInputFiles.map((file) => (
                    <FilePill key={file.id} file={file} />
                  ))}
                  {uploadQueue.length === 0 && uploadedInputFiles.length === 0 ? (
                    <p className="muted">Upload agreements, subscription docs, or source CSV files before you ask.</p>
                  ) : null}
                </div>
                <div className="file-group">
                  <div className="file-group-heading">
                    <strong>Generated Outputs</strong>
                    <span>{generatedArtifacts.length}</span>
                  </div>
                  {generatedArtifacts.map((file) => (
                    <FilePill key={file.id} file={file} generated />
                  ))}
                  {generatedArtifacts.length === 0 ? (
                    <p className="muted">Review workbooks, template outputs, and other generated artifacts will appear here.</p>
                  ) : null}
                </div>
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

function FilePill({
  file,
  pending = false,
  generated = false,
}: {
  file: FileAsset;
  pending?: boolean;
  generated?: boolean;
}) {
  const canDownload = !pending;

  return (
    <button
      type="button"
      className={`file-pill ${pending ? "pending" : ""} ${generated ? "generated" : ""} ${canDownload ? "downloadable" : ""}`}
      onClick={canDownload ? () => openFileDownload(file.id) : undefined}
      disabled={!canDownload}
      title={canDownload ? `Download ${file.fileName}` : "File will be downloadable after upload completes"}
    >
      <div>
        <strong>{file.fileName}</strong>
        <span>{formatBytes(file.sizeBytes)}</span>
      </div>
      <em>{pending ? "queued for send" : generated ? "generated" : "uploaded"}</em>
    </button>
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
  onSubmit,
}: {
  task: ReviewTask;
  operations: AgentOperation[];
  onSubmit: (
    action: string,
    options?: {
      finalPayloadJson?: string;
      changeRequestText?: string;
      notes?: string;
    },
  ) => void;
}) {
  const operation = operations.find((candidate) => candidate.id === task.operationId);
  const [draftPayload, setDraftPayload] = useState(prettyJson(task.finalPayloadJson ?? task.proposedPayloadJson));
  const [changeRequestText, setChangeRequestText] = useState("");
  const [isRawEditorOpen, setIsRawEditorOpen] = useState(false);
  const [payloadError, setPayloadError] = useState<string | null>(null);

  useEffect(() => {
    setDraftPayload(prettyJson(task.finalPayloadJson ?? task.proposedPayloadJson));
    setChangeRequestText("");
    setIsRawEditorOpen(false);
    setPayloadError(null);
  }, [task.id, task.finalPayloadJson, task.proposedPayloadJson]);

  const isEditable = task.interactionMode === "EditAndSubmit";
  const capitalCallReviewPayload = task.taskType === "CapitalCallExtractionReview"
    ? parseCapitalCallReviewPayload(task.proposedPayloadJson)
    : null;
  const capitalCallConfirmationPayload = task.taskType === "CapitalCallAllocationConfirmation"
    ? parseCapitalCallConfirmationPayload(task.proposedPayloadJson)
    : null;
  const capitalCallPreviewSource = capitalCallReviewPayload ?? capitalCallConfirmationPayload?.reviewedExtraction ?? null;
  const capitalCallFund = capitalCallPreviewSource?.rootFund ?? null;
  const capitalCallNotice = capitalCallConfirmationPayload?.notice ?? null;
  const previewRows = buildCapitalCallPreviewRows(capitalCallFund).slice(0, 6);
  const leafAllocationRows = capitalCallNotice?.leafAllocations?.slice(0, 6) ?? [];
  const primaryLabel = isEditable ? "Submit Reviewed Data" : "Approve";
  const originalNormalizedPayload = normalizeJson(task.finalPayloadJson ?? task.proposedPayloadJson);
  const draftNormalizedPayload = normalizeJson(draftPayload);
  const hasStructuredChanges = originalNormalizedPayload !== null &&
    draftNormalizedPayload !== null &&
    originalNormalizedPayload !== draftNormalizedPayload;

  function handlePrimaryAction() {
    if (!isEditable) {
      onSubmit("Approved", { finalPayloadJson: task.proposedPayloadJson });
      return;
    }

    if (hasStructuredChanges) {
      try {
        const normalized = JSON.stringify(JSON.parse(draftPayload), null, 2);
        setPayloadError(null);
        onSubmit("Submitted", {
          finalPayloadJson: normalized,
          notes: "Reviewed with structured payload edits from the review queue.",
        });
        return;
      } catch {
        setPayloadError("The edited payload is not valid JSON yet. Fix it before submitting.");
        return;
      }
    }

    if (changeRequestText.trim()) {
      setPayloadError(null);
      onSubmit("Submitted", {
        changeRequestText: changeRequestText.trim(),
        notes: "Reviewed with natural-language change request from the review queue.",
      });
      return;
    }

    setPayloadError("Add a change request in natural language, edit the payload directly, or use Approve as-is.");
  }

  function handleApproveAsIs() {
    setPayloadError(null);
    onSubmit("Approved", {
      finalPayloadJson: task.proposedPayloadJson,
      notes: "Approved as-is from the review queue.",
    });
  }

  function handleReject() {
    onSubmit("Rejected", {
      finalPayloadJson: hasStructuredChanges ? draftPayload : task.proposedPayloadJson,
      changeRequestText: changeRequestText.trim() || undefined,
      notes: "Rejected from the review queue.",
    });
  }

  return (
    <article className="review-card">
      <div className="card-topline">
        <strong>{task.title}</strong>
        <span>{task.taskType}</span>
      </div>
      <p>{operation?.title ?? "Unknown operation"}</p>
      {task.instructionText ? <p className="review-instruction">{task.instructionText}</p> : null}
      {capitalCallFund ? (
        <div className="review-preview">
          <div className="review-preview-header">
            <strong>{capitalCallFund.fundName ?? "Capital Call Partner Data"}</strong>
            <span>{capitalCallFund.fundCurrency ?? "Currency pending"}</span>
          </div>
          <div className="review-preview-meta">
            <span>{countFundNodes(capitalCallFund)} funds in tree</span>
            <span>{countPartners(capitalCallFund)} partners extracted</span>
          </div>
          {previewRows.map((allocation, index) => (
            <div className="review-preview-row" key={`${allocation.partnerName}-${index}`}>
              <span>{allocation.partnerName}</span>
              <strong>{allocation.commitmentPercentage?.toFixed(2) ?? "0.00"}%</strong>
            </div>
          ))}
        </div>
      ) : null}
      {capitalCallNotice ? (
        <div className="review-preview">
          <div className="review-preview-header">
            <strong>{capitalCallNotice.rootFundName ?? "Capital Call Allocation Summary"}</strong>
            <span>{capitalCallNotice.rootCurrency ?? "Currency pending"}</span>
          </div>
          <div className="review-preview-meta">
            <span>{formatCurrencyAmount(capitalCallNotice.rootCapitalCallAmount, capitalCallNotice.rootCurrency)}</span>
            <span>{capitalCallNotice.fundBreakdowns?.length ?? 0} fund breakdowns</span>
            <span>{capitalCallNotice.leafAllocations?.length ?? 0} investor allocations</span>
          </div>
          {leafAllocationRows.map((allocation, index) => (
            <div className="review-preview-row" key={`${allocation.investorName}-${index}`}>
              <span>
                {allocation.investorName ?? "Investor"}
                {allocation.parentFundPath ? ` · ${allocation.parentFundPath}` : ""}
              </span>
              <strong>{formatCurrencyAmount(allocation.contributionAmount, allocation.currency)}</strong>
            </div>
          ))}
        </div>
      ) : null}
      {capitalCallPreviewSource?.reviewDownloadRoute ? (
        <button
          className="secondary-button review-download-button"
          type="button"
          onClick={() => {
            const baseUrl = import.meta.env.VITE_API_BASE_URL ?? "http://localhost:8080";
            window.open(new URL(capitalCallPreviewSource.reviewDownloadRoute!, baseUrl).toString(), "_blank", "noopener,noreferrer");
          }}
        >
          Download Review Workbook
          {capitalCallPreviewSource.reviewFileName ? `: ${capitalCallPreviewSource.reviewFileName}` : ""}
        </button>
      ) : null}
      {isEditable ? (
        <div className="review-change-box">
          <label className="review-label" htmlFor={`review-change-${task.id}`}>
            Tell me what to change
          </label>
          <textarea
            id={`review-change-${task.id}`}
            className="review-change-textarea"
            value={changeRequestText}
            onChange={(event) => setChangeRequestText(event.target.value)}
            placeholder="Example: Change North Star Feeder to 55% and keep Apex USD Feeder under the master fund."
            rows={4}
          />
          <button
            className="ghost-button review-advanced-toggle"
            type="button"
            onClick={() => setIsRawEditorOpen((current) => !current)}
          >
            {isRawEditorOpen ? "Hide advanced payload editor" : "Advanced: edit raw payload"}
          </button>
        </div>
      ) : null}
      <div className="review-actions">
        <button className="secondary-button" type="button" onClick={handleReject}>
          Reject
        </button>
        {isEditable ? (
          <button className="secondary-button" type="button" onClick={handleApproveAsIs}>
            Approve as-is
          </button>
        ) : null}
        <button className="primary-button review-approve-button" type="button" onClick={handlePrimaryAction}>
          {primaryLabel}
        </button>
      </div>
      {isEditable && isRawEditorOpen ? (
        <>
          <textarea
            className="review-payload-editor"
            value={draftPayload}
            onChange={(event) => setDraftPayload(event.target.value)}
            spellCheck={false}
            rows={14}
          />
          {payloadError ? <p className="review-error">{payloadError}</p> : null}
        </>
      ) : !isEditable && !capitalCallNotice ? (
        <pre>{task.proposedPayloadJson}</pre>
      ) : payloadError ? <p className="review-error">{payloadError}</p> : null}
    </article>
  );
}

function MessageCard({
  message,
  operations,
  onAction,
}: {
  message: ConversationMessage;
  operations: AgentOperation[];
  onAction: (action: AgentAction) => void | Promise<void>;
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
              onClick={() => void onAction(action)}
            >
              {action.label}
            </button>
          ))}
        </div>
      ) : null}
    </article>
  );
}
