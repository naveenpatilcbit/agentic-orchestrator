# Fund Orchestrator System Design

## 1. Purpose

This document describes the architecture for an AI-assisted fund administration orchestration layer built on top of a private equity SaaS product.

Primary users:

- Fund Accountants
- Fund Admins
- Internal support and operations teams

Primary product goal:

- Let users interact through an in-app chat surface
- Route each request to the right agent capability
- Execute simple inline actions and long-running workflows safely
- Keep all meaningful work auditable, tenant-scoped, and review-first

This repo contains a working sample implementation of that architecture using:

- `.NET 9`
- `MongoDB`
- `Microsoft Agent Framework Workflows`
- `MongoDB-backed workflow checkpoints and pending-request state`
- `React + Vite`
- `Microsoft.Extensions.AI` as the application-facing LLM abstraction

## 2. Design Principles

- Chat is the user-facing control surface, not the workflow source of truth.
- One conversation can contain many operations.
- Every meaningful unit of work gets its own `OperationId`.
- Sensitive or record-creating flows end in draft or review unless trivially reversible.
- Agents return structured actions, not only free-form text.
- Long-running workflows must survive delays, polling, retries, and human review.
- Tenant isolation is enforced in storage and APIs, not delegated to prompts.
- Auditability is built into every stage: message, routing, agent result, review, and workflow continuation.
- LLM usage is provider-neutral at the application boundary; provider SDKs stay isolated in infrastructure.

## 3. Scope

### In scope

- Chat-driven request intake
- Agent routing
- Inline agent execution
- workflow-backed onboarding orchestration with resumable checkpoints
- Human review queue
- Conversation history
- Conversation thread projection for workflow updates
- Audit event persistence
- Tenant-scoped storage

### Example agents

- Notice Creation Helper
- Fund Onboarding Helper
- One Pager Generation Agent

### Out of scope in this sample

- Real authentication and authorization
- Real OCR/extraction integration
- Production-grade multi-provider LLM operations such as evaluation, fallback routing, prompt registry, and centralized caching
- Real notification channels beyond polling/chat updates
- Production-grade deployment hardening

## 4. Core Domain Model

### Conversation

A conversation is the user-facing thread.

- `ConversationId`
- `TenantId`
- `Title`
- `LastFocusedOperationId`
- `CreatedAtUtc`
- `UpdatedAtUtc`

### Operation

An operation is the runtime unit of work.

- `OperationId`
- `ConversationId`
- `TenantId`
- `AgentId`
- `Status`
- `CurrentStep`
- `Summary`
- `PendingClarification`
- `ActiveReviewTaskId`
- `DataJson`

### Review Task

A review task is a human approval or correction checkpoint attached to an operation.

- `ReviewTaskId`
- `ConversationId`
- `OperationId`
- `TaskType`
- `Status`
- `ProposedPayloadJson`
- `FinalPayloadJson`
- `Notes`

### Conversation Message

A conversation message is the rendered chat projection.

- user prompt
- assistant reply
- workflow/system update
- review status update

### Audit Event

An append-only event emitted by user actions, agents, and workflows.

## 5. High-Level Architecture

```mermaid
flowchart LR
    U["Fund Admin / Fund Accountant"] --> UI["React Chat UI"]
    UI --> API["Orchestrator API (.NET 9)"]

    API --> ROUTER["Message Routing Service"]
    API --> CHAT["Chat Orchestrator Service"]
    API --> REV["Review Task Service"]
    API --> FILES["File Upload Service"]
    API --> LLM["LLM Interaction Layer (Microsoft.Extensions.AI)"]

    ROUTER --> AGENTS["Agent Catalog"]
    AGENTS --> NOTICE["Notice Creation Agent"]
    AGENTS --> ONEPAGER["One Pager Agent"]
    AGENTS --> ONBOARD["Fund Onboarding Agent"]
    ROUTER --> LLM
    NOTICE --> LLM
    ONEPAGER --> LLM
    LLM --> PROVIDER["Provider Adapter (swappable)"]
    PROVIDER --> MODEL["Model Provider Endpoint"]

    ONBOARD --> WFRT["Workflow Runtime Service"]
    WFRT --> WFLOW["Fund Onboarding Workflow"]
    WFRT --> WFCHECK["Mongo Checkpoint Store"]

    CHAT --> MONGO[("MongoDB")]
    REV --> MONGO
    WFRT --> MONGO
    FILES --> DISK["Local File Storage"]
```

## 6. Component Responsibilities

### React UI

- chat composer
- message thread
- previous thread history
- active work list
- review queue
- polling conversation snapshots

### Orchestrator API

- accepts chat messages
- lists conversations
- returns conversation snapshots
- accepts review decisions
- accepts document uploads
- injects mocked tenant context

### Message Routing Service

Determines whether a message:

- continues an existing operation
- responds to a review task
- asks for status
- starts new work
- is ambiguous and needs clarification

The recommended implementation uses an LLM-backed structured classifier behind a provider-neutral abstraction. The routing service should depend on `Microsoft.Extensions.AI` interfaces or a thin application wrapper on top of them, not on a provider SDK directly.

### Agent Catalog

Registers supported capabilities and provides agent lookup/classification.

### Inline Agents

- `NoticeCreationAgent`
- `OnePagerAgent`

These should use a shared input-completion layer:

- required fields are defined in code by the agent/workflow contract
- an LLM extracts candidate values from the latest user message plus small operation-scoped history
- validation of required fields remains deterministic in code
- execution proceeds only after required fields are present

This keeps the system safe while still making clarification loops natural for users.

### LLM Interaction Layer

This layer is responsible for all model calls that support routing and agent slot-filling.

- application code depends on `Microsoft.Extensions.AI` abstractions such as `IChatClient`
- infrastructure provides the concrete adapter package
- the current default adapter can be `Microsoft.Extensions.AI.OpenAI`
- the underlying provider SDK such as `OpenAI` should stay hidden behind the infrastructure adapter
- a future swap to Azure OpenAI, Anthropic, Gemini, or another OpenAI-compatible endpoint should require only infrastructure changes
- the application should expose narrow use-case-specific services such as:
  - `IMessageIntentClassifier`
  - `IAgentInputCompletionService`
  - optional future `IStructuredLlmClient`

This avoids vendor lock-in while keeping the business code focused on orchestration and validation.

Recommended package strategy:

- application-facing abstraction: `Microsoft.Extensions.AI`
- current adapter: `Microsoft.Extensions.AI.OpenAI`
- current provider SDK hidden in infrastructure: `OpenAI`

### Workflow Runtime

`FundOnboardingWorkflow` manages:

- onboarding start
- create classification review
- continue after approval
- create extraction review
- complete with draft creation
- resume from Mongo-backed checkpoints after each review

### MongoDB

Stores:

- conversations
- messages
- operations
- review tasks
- audit events
- file metadata
- workflow instances
- workflow pending requests
- workflow checkpoints

## 7. Execution Model

### Agent execution modes

- `InlineFunction`
- `Workflow`

### Current agent mapping

| Agent | Mode | Runtime shape |
| --- | --- | --- |
| Notice Creation Helper | `InlineFunction` | run LLM-assisted slot filling, validate required inputs, create draft action |
| One Pager Generation Agent | `InlineFunction` | run LLM-assisted slot filling, validate required inputs, generate artifact action |
| Fund Onboarding Helper | `Workflow` | workflow runtime, Mongo checkpoints, review gates |

### LLM-assisted request completion

For inline and hybrid agents, request completion should follow this pattern:

1. Load the current operation state
2. Read the agent-defined field schema and required inputs
3. Send the latest user message plus a small operation-scoped history window to the LLM interaction layer
4. Merge any extracted values into the operation payload
5. Determine missing required fields using deterministic validation
6. Either ask for clarification or continue execution

Important rule:

- the LLM extracts values
- the code decides what fields are mandatory
- the code decides whether execution is allowed

This is the core mechanism that keeps `ContinueAsync` safe and predictable.

### Why not “everything is just a kernel function”

Simple functions are fine as an implementation detail. They are not enough as a platform boundary. The stable platform boundary is:

- conversation
- operation
- review task
- audit event
- workflow continuation

That shared runtime model keeps new agents pluggable and governable.

## 8. Message Routing Policy

Each incoming message must resolve to one of:

- `ContinueOperation`
- `RespondToReviewTask`
- `AskStatus`
- `StartNewOperation`
- `AmbiguousNeedClarification`

### Decision order

1. Match explicit operation reference
2. Match explicit or implied review decision
3. Match status query
4. Classify as new supported work
5. Route to clarification-required operation if one exists
6. Use referenced or single active operation
7. Ask for clarification if several interpretations remain

### Important behavior

One conversation can contain multiple active operations at the same time. A new request should not hijack an existing workflow unless the message clearly continues that workflow.

## 8.1 Scenario Catalog

The table below captures the expected behavior for the most important runtime situations in this product.

| Scenario | Expected system behavior | Persisted outcome |
| --- | --- | --- |
| User starts a brand-new request | Route to a supported agent and create a new operation | new conversation if needed, new operation, audit event |
| User sends message that fills missing fields | Continue the clarification-required operation | same operation updated, pending clarification cleared |
| User asks for notice creation with enough details | Complete inline and return draft action | operation completed, draft route stored |
| User asks for notice creation with missing details | Ask for more information | operation set to `ClarificationRequired` |
| User uploads docs and asks to create fund | Start onboarding workflow | operation created, workflow instance/checkpoint state persisted |
| Workflow reaches classification checkpoint | Create review task and system chat update | task stored, operation set to `WaitingForHumanReview` |
| Reviewer approves classification | Resume workflow and request extraction stage | review task approved, operation resumed, continuation command published |
| Workflow reaches extraction checkpoint | Create extraction review task | task stored, operation set to `WaitingForHumanReview` |
| Reviewer approves extraction | Complete operation and create draft-ready state | operation completed, route persisted, audit event appended |
| Reviewer rejects a task | Pause operation for correction | review task rejected, operation set to `ClarificationRequired` |
| User asks for status with one active operation | Return status for that operation | assistant/system message appended |
| User asks for status with multiple active operations | Return summary of all active work, or specific one if referenced | assistant/system message appended |
| User asks a different question mid-workflow | Start a new operation if the request is clearly new | second operation in same conversation |
| User says “approve it” and only one review is open | Auto-route to that review task | review decision stored, workflow may continue |
| User says “approve it” and multiple reviews are open | Ask for clarification | no workflow resumed until clarified |
| User opens a previous thread | Load summaries then full snapshot | conversation history list + selected snapshot |
| External or workflow stage fails | Mark operation as failed and surface message | operation set to `Failed`, audit event appended |
| User retries after failure | Start fresh work or explicit resume flow, depending on future policy | new operation or retried state |

## 9. State Machines

### Agent operation state machine

```mermaid
stateDiagram-v2
    [*] --> Received

    Received --> ClarificationRequired: Missing inputs
    ClarificationRequired --> Running: Clarification supplied

    Received --> Running: Inline execution starts
    Received --> WaitingForExternalSystem: Workflow submitted

    Running --> WaitingForHumanReview: Review task created
    WaitingForHumanReview --> Running: Review approved
    WaitingForHumanReview --> ClarificationRequired: Review rejected

    Running --> WaitingForExternalSystem: External stage submitted
    WaitingForExternalSystem --> WaitingForHumanReview: External result ready

    Running --> Completed: Finished successfully
    WaitingForHumanReview --> Completed: Final approval completes work

    Received --> Failed
    Running --> Failed
    WaitingForExternalSystem --> Failed
    WaitingForHumanReview --> Failed

    Received --> Cancelled
    Running --> Cancelled
    WaitingForExternalSystem --> Cancelled
    WaitingForHumanReview --> Cancelled
```

### Review task state machine

```mermaid
stateDiagram-v2
    [*] --> Open
    Open --> Approved
    Open --> Rejected
    Open --> NeedsChanges
```

## 10. End-to-End Sequence Diagrams

### 10.1 Common chat-to-agent runtime

```mermaid
sequenceDiagram
    autonumber
    participant U as User
    participant UI as "React UI"
    participant API as "Orchestrator API"
    participant CHAT as "Chat Orchestrator"
    participant ROUTER as "Message Routing Service"
    participant LLM as "LLM Interaction Layer"
    participant AGENT as "Selected Agent"
    participant DB as "MongoDB"

    U->>UI: Send message
    UI->>API: POST /api/chat/messages
    API->>CHAT: HandleMessageAsync
    CHAT->>DB: Store user message
    CHAT->>ROUTER: Decide(message, conversation, operations, reviewTasks)
    ROUTER->>LLM: Structured intent classification
    LLM-->>ROUTER: Routing decision JSON
    ROUTER-->>CHAT: Routing decision
    CHAT->>AGENT: Start or continue operation
    AGENT-->>CHAT: AgentExecutionResult
    CHAT->>DB: Upsert operation
    CHAT->>DB: Append assistant/system messages
    CHAT->>DB: Append audit events
    CHAT-->>API: Conversation snapshot
    API-->>UI: Updated thread
```

### 10.2 Notice Creation Helper

```mermaid
sequenceDiagram
    autonumber
    participant U as User
    participant UI as "React UI"
    participant API as "Orchestrator API"
    participant CHAT as "Chat Orchestrator"
    participant ROUTER as "Routing Service"
    participant AGENT as "Notice Creation Agent"
    participant LLM as "LLM Interaction Layer"
    participant DB as "MongoDB"

    U->>UI: "Create a capital call notice for Apex Fund I for $3,500,000"
    UI->>API: POST message
    API->>CHAT: HandleMessageAsync
    CHAT->>ROUTER: Classify new work
    ROUTER-->>CHAT: Start notice operation
    CHAT->>DB: Create operation
    CHAT->>AGENT: StartAsync
    AGENT->>LLM: Extract structured fields from latest message + operation history
    LLM-->>AGENT: fundName, amount, optional noticeDate
    AGENT->>AGENT: Validate required inputs in code

    alt Missing data
        AGENT-->>CHAT: ClarificationRequired + AskForMoreInfo
        CHAT->>DB: Save operation state and message
        CHAT-->>UI: Ask for missing inputs
    else Enough data
        AGENT-->>CHAT: Completed + OpenPageWithPrefill action
        CHAT->>DB: Save draft-ready operation and audit event
        CHAT-->>UI: Render draft action
    end
```

### 10.3 Fund Onboarding Helper with workflow checkpoints

```mermaid
sequenceDiagram
    autonumber
    participant U as User
    participant UI as "React UI"
    participant API as "Orchestrator API"
    participant CHAT as "Chat Orchestrator"
    participant AGENT as "Fund Onboarding Agent"
    participant WF as "Workflow Runtime"
    participant CP as "Mongo Checkpoint Store"
    participant DB as "MongoDB"

    U->>UI: Upload docs and ask to create fund
    UI->>API: POST files
    API->>DB: Save file metadata
    U->>UI: "Create fund from these documents"
    UI->>API: POST message
    API->>CHAT: HandleMessageAsync
    CHAT->>AGENT: StartAsync
    AGENT->>DB: Create operation
    AGENT->>WF: StartAsync(start input)
    WF->>CP: Save workflow checkpoint
    WF->>DB: Persist workflow instance + pending request
    WF->>DB: Create classification review task
    WF->>DB: Update operation to WaitingForHumanReview
    WF->>DB: Add system chat update
    CHAT-->>UI: Operation started
    UI->>API: Poll conversation snapshot
    API->>DB: Load latest messages, tasks, operations
    API-->>UI: Classification review is ready
```

### 10.4 Review approval and workflow continuation

```mermaid
sequenceDiagram
    autonumber
    participant U as Reviewer
    participant UI as "React UI"
    participant API as "Review API"
    participant REV as "Review Task Service"
    participant WF as "Workflow Runtime"
    participant CP as "Mongo Checkpoint Store"
    participant DB as "MongoDB"

    U->>UI: Approve review task
    UI->>API: POST /api/reviews/{id}/decision
    API->>REV: SubmitAsync
    REV->>DB: Update review task to Approved
    REV->>DB: Update operation to Running / ResumeRequested
    REV->>DB: Append system chat message
    REV->>DB: Append audit event
    REV->>WF: ResumeAsync(response payload)
    WF->>CP: Restore latest checkpoint
    alt Classification review approved
        WF->>CP: Save next checkpoint
        WF->>DB: Create extraction review task
        WF->>DB: Set operation WaitingForHumanReview
        WF->>DB: Add workflow chat update
    else Extraction review approved
        WF->>CP: Save completion checkpoint
        WF->>DB: Mark operation Completed
        WF->>DB: Store draft route in operation data
        WF->>DB: Add workflow chat update
        WF->>DB: Append completion audit event
    end
```

### 10.5 One Pager Generation Agent

```mermaid
sequenceDiagram
    autonumber
    participant U as User
    participant UI as "React UI"
    participant API as "Orchestrator API"
    participant CHAT as "Chat Orchestrator"
    participant ROUTER as "Routing Service"
    participant AGENT as "One Pager Agent"
    participant LLM as "LLM Interaction Layer"
    participant DB as "MongoDB"

    U->>UI: Upload format and ask for one-pager
    UI->>API: POST files
    API->>DB: Save file metadata
    U->>UI: "Generate a one-pager for BlueWave Systems"
    UI->>API: POST message
    API->>CHAT: HandleMessageAsync
    CHAT->>ROUTER: Classify new work
    ROUTER-->>CHAT: Start one-pager operation
    CHAT->>AGENT: StartAsync
    AGENT->>LLM: Extract company and template from prompt + scoped history
    LLM-->>AGENT: structured candidate values
    AGENT->>AGENT: Validate required inputs in code

    alt Missing company
        AGENT-->>CHAT: ClarificationRequired
        CHAT->>DB: Save state and assistant prompt
    else Ready
        AGENT-->>CHAT: Completed + DownloadArtifact action
        CHAT->>DB: Save operation and audit event
        CHAT-->>UI: Render one-pager draft action
    end
```

### 10.6 Same conversation, different request mid-workflow

```mermaid
sequenceDiagram
    autonumber
    participant U as User
    participant UI as "React UI"
    participant API as "Orchestrator API"
    participant CHAT as "Chat Orchestrator"
    participant ROUTER as "Routing Service"
    participant DB as "MongoDB"

    Note over DB: Operation A = Fund Onboarding, status WaitingForHumanReview

    U->>UI: "Generate a one-pager for BlueWave Systems"
    UI->>API: POST message in same conversation
    API->>CHAT: HandleMessageAsync
    CHAT->>DB: Load active operations and open review tasks
    CHAT->>ROUTER: Decide(message, operations, reviewTasks)
    ROUTER-->>CHAT: StartNewOperation for One Pager
    CHAT->>DB: Create Operation B
    CHAT->>DB: Keep Operation A unchanged
    CHAT-->>UI: Same conversation now shows multiple operations

    Note over UI: Conversation remains single-threaded for the user
    Note over DB: Execution remains isolated by OperationId
```

### 10.7 Previous conversation threads

```mermaid
sequenceDiagram
    autonumber
    participant UI as "React UI"
    participant API as "Orchestrator API"
    participant CHAT as "Chat Orchestrator"
    participant DB as "MongoDB"

    UI->>API: GET /api/chat/conversations
    API->>CHAT: ListConversationsAsync
    CHAT->>DB: Load tenant conversations
    CHAT->>DB: Count messages, active operations, open reviews
    CHAT-->>API: Conversation summaries
    API-->>UI: Thread history list
    UI->>API: GET /api/chat/conversations/{conversationId}
    API->>CHAT: GetSnapshotAsync
    CHAT->>DB: Load full conversation snapshot
    API-->>UI: Selected thread restored
```

### 10.8 Clarification loop for inline agents

```mermaid
sequenceDiagram
    autonumber
    participant U as User
    participant UI as "React UI"
    participant API as "Orchestrator API"
    participant CHAT as "Chat Orchestrator"
    participant ROUTER as "Routing Service"
    participant AGENT as "Notice or One Pager Agent"
    participant LLM as "LLM Interaction Layer"
    participant DB as "MongoDB"

    U->>UI: Initial request with missing fields
    UI->>API: POST message
    API->>CHAT: HandleMessageAsync
    CHAT->>ROUTER: Decide
    ROUTER-->>CHAT: StartNewOperation
    CHAT->>AGENT: StartAsync
    AGENT->>LLM: Extract available field values
    LLM-->>AGENT: partial field set
    AGENT-->>CHAT: ClarificationRequired
    CHAT->>DB: Save operation state
    CHAT-->>UI: Ask for missing fields

    U->>UI: Provide missing details
    UI->>API: POST follow-up message
    API->>CHAT: HandleMessageAsync
    CHAT->>ROUTER: Decide
    ROUTER-->>CHAT: ContinueOperation
    CHAT->>AGENT: ContinueAsync
    AGENT->>LLM: Extract newly provided fields from follow-up
    LLM-->>AGENT: updated field values
    AGENT-->>CHAT: Completed with action
    CHAT->>DB: Update operation and append assistant reply
    CHAT-->>UI: Show final draft or artifact action
```

### 10.9 Ambiguous approval when multiple review tasks are open

```mermaid
sequenceDiagram
    autonumber
    participant U as User
    participant UI as "React UI"
    participant API as "Orchestrator API"
    participant CHAT as "Chat Orchestrator"
    participant ROUTER as "Routing Service"
    participant DB as "MongoDB"

    Note over DB: Two review tasks are open in the same conversation

    U->>UI: "Approve it"
    UI->>API: POST message
    API->>CHAT: HandleMessageAsync
    CHAT->>DB: Load open review tasks
    CHAT->>ROUTER: Decide
    ROUTER-->>CHAT: AmbiguousNeedClarification
    CHAT->>DB: Append assistant clarification message
    CHAT-->>UI: Ask user which review task to approve
```

### 10.10 Status query while multiple operations are active

```mermaid
sequenceDiagram
    autonumber
    participant U as User
    participant UI as "React UI"
    participant API as "Orchestrator API"
    participant CHAT as "Chat Orchestrator"
    participant ROUTER as "Routing Service"
    participant DB as "MongoDB"

    Note over DB: Multiple active operations exist for the same conversation

    U->>UI: "What's the status?"
    UI->>API: POST message
    API->>CHAT: HandleMessageAsync
    CHAT->>ROUTER: Decide
    ROUTER-->>CHAT: AskStatus
    CHAT->>DB: Load active operations
    CHAT->>DB: Append status summary message
    CHAT-->>UI: Show active work summary in-thread
```

### 10.11 Review rejection path

```mermaid
sequenceDiagram
    autonumber
    participant U as Reviewer
    participant UI as "React UI"
    participant API as "Review API"
    participant REV as "Review Task Service"
    participant DB as "MongoDB"

    U->>UI: Reject review task
    UI->>API: POST /api/reviews/{id}/decision
    API->>REV: SubmitAsync
    REV->>DB: Mark review task Rejected
    REV->>DB: Set operation to ClarificationRequired
    REV->>DB: Store reviewer notes and final payload
    REV->>DB: Append system chat message
    REV->>DB: Append audit event
    API-->>UI: Updated conversation snapshot
```

### 10.12 Failure and recovery path

```mermaid
sequenceDiagram
    autonumber
    participant WORK as "Agent or Saga Worker"
    participant DB as "MongoDB"
    participant UI as "React UI"
    participant API as "Orchestrator API"

    WORK->>DB: Detect execution or integration failure
    WORK->>DB: Mark operation Failed
    WORK->>DB: Append audit event
    WORK->>DB: Append system message with failure summary
    UI->>API: Poll conversation snapshot
    API->>DB: Load latest operation state
    API-->>UI: Show failed status and preserved thread context
```

## 11. Conversation Projection Model

The workflow does not “continue from chat memory”. It continues from persisted workflow state.

Chat is updated as a projection of backend state changes:

- user sends prompt
- operation is created or updated
- review is created
- review is approved or rejected
- workflow runtime resumes from checkpoint
- system appends a chat update

This separation gives:

- resumability
- cleaner retries
- stronger auditability
- support for multiple operations in one thread

## 12. Data Isolation and Auditability

### Tenant isolation

Every major entity is tenant-scoped:

- conversation
- message
- operation
- review task
- audit event
- file metadata

The sample uses mocked tenant headers, but the storage boundary is already tenant-aware.

### Audit coverage

Events should be emitted for:

- message received
- agent selected
- operation created
- clarification requested
- draft created
- workflow started
- review created
- review submitted
- workflow resumed
- workflow completed
- workflow failed
- ambiguity clarification requested
- status query answered

## 12.1 Failure Handling

### Inline agents

- validation failures move the operation to `ClarificationRequired` when the user can correct the issue
- unexpected exceptions should move the operation to `Failed`
- a failure message should be appended to the chat thread

### Saga workflows

- transient failures should be retried by the transport/handler policy
- non-recoverable failures should mark the operation as `Failed`
- review tasks should not be orphaned silently
- the conversation thread should show that the workflow stopped and needs intervention

### Recovery principle

Recovery should happen from persisted operation and review state, not by replaying free-form chat messages.

## 13. API Surface

### Chat

- `POST /api/chat/messages`
- `GET /api/chat/conversations/{conversationId}`
- `GET /api/chat/conversations`

### Review

- `POST /api/reviews/{reviewTaskId}/decision`

### Files

- `POST /api/files/upload`

## 14. Current Implementation Mapping

### Backend

- routing: `backend/src/Framework/ConversationalOrchestration.Application/Operations/MessageRoutingService.cs`
- orchestration: `backend/src/Framework/ConversationalOrchestration.Application/Conversations/ChatOrchestratorService.cs`
- review continuation: `backend/src/Framework/ConversationalOrchestration.Application/Reviews/ReviewTaskService.cs`
- generic agent catalog: `backend/src/Framework/ConversationalOrchestration.Application/Agents/AgentCatalog.cs`
- application LLM abstractions: `backend/src/Framework/ConversationalOrchestration.Application/Abstractions/StructuredLlm.cs`
- provider-neutral LLM gateway: `backend/src/Framework/ConversationalOrchestration.Infrastructure/AI/MicrosoftExtensionsAiStructuredLlmClient.cs`
- routing classifier: `backend/src/Framework/ConversationalOrchestration.Infrastructure/AI/LlmMessageIntentClassifier.cs`
- input completion: `backend/src/Framework/ConversationalOrchestration.Infrastructure/AI/LlmAgentInputCompletionService.cs`
- fund-domain agents: `backend/src/Domain/ConversationalOrchestration.FundAdministration/Agents`
- workflow runtime: `backend/src/Framework/ConversationalOrchestration.Infrastructure/Workflows/WorkflowRuntime.cs`
- Mongo checkpoint store: `backend/src/Framework/ConversationalOrchestration.Infrastructure/Workflows/MongoJsonCheckpointStore.cs`
- fund onboarding workflow: `backend/src/Domain/ConversationalOrchestration.FundAdministration/Workflows/FundOnboardingWorkflowDefinition.cs`
- contracts: `backend/src/Framework/ConversationalOrchestration.Contracts`
- repositories: `backend/src/Framework/ConversationalOrchestration.Infrastructure/Repositories/MongoRepositories.cs`

### Frontend

- main app: `frontend/app/src/App.tsx`
- styling: `frontend/app/src/App.css`
- API client: `frontend/app/src/api.ts`
- DTO types: `frontend/app/src/types.ts`

## 15. Tradeoffs

### Why this works well now

- low user concurrency
- clear plugin path for new agents
- durable handling for long-running onboarding with checkpoint-based resume/retry
- safe support for multiple operations in one conversation
- strong fit for review-first private equity workflows

### Known simplifications in this sample

- the application is provider-neutral, but the infrastructure currently ships with one OpenAI-compatible adapter
- onboarding uses dummy workflow steps instead of a real external OCR/extraction service
- file storage is local, not object storage
- polling is used instead of SignalR
- auth is mocked

## 16. Recommended Next Steps

- add real policy-based authorization
- replace dummy onboarding workflow steps with external service callbacks or polling adapters
- centralize all model access behind `Microsoft.Extensions.AI` and a small internal LLM gateway
- add agent-specific prompt/version tracking for routing and input-completion profiles
- move file storage to object storage
- add SignalR for real-time status updates
- introduce agent registry configuration so new agents can be enabled per tenant/environment
- add provider fallback, evals, and cache/telemetry around the LLM gateway
