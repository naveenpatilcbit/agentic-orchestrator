# Conversational Orchestration

Reusable conversational workflow framework plus a fund-administration domain module.

This repo currently demonstrates:

- `.NET 9` clean-architecture backend
- `React + Vite` frontend with chat, previous threads, review queue, active work, uploaded inputs, and generated artifacts
- `MongoDB` for conversations, operations, review tasks, audit events, files, workflow instances, pending requests, and checkpoints
- `Microsoft Agent Framework Workflows` as the workflow engine
- Mongo-backed checkpoint persistence with resume and retry support
- provider-neutral LLM abstraction via `Microsoft.Extensions.AI`
- fund-administration sample agents for:
  - capital call notice creation
  - fund onboarding
  - one-pager generation

Design document:

- [docs/system-design.md](/Users/naveenkumarpatil/Documents/orchestrator%20design/docs/system-design.md)
- [docs/chat-history-and-compaction.md](/Users/naveenkumarpatil/Documents/orchestrator%20design/docs/chat-history-and-compaction.md)
- [docs/capital-call-notice-flow.md](/Users/naveenkumarpatil/Documents/orchestrator%20design/docs/capital-call-notice-flow.md)

## Architecture

The repo is organized as a reusable framework plus a domain module in the same solution.

Backend solution:

- [backend/ConversationalOrchestration.sln](/Users/naveenkumarpatil/Documents/orchestrator%20design/backend/ConversationalOrchestration.sln)

Framework projects:

- [ConversationalOrchestration.Domain](/Users/naveenkumarpatil/Documents/orchestrator%20design/backend/src/Framework/ConversationalOrchestration.Domain)
- [ConversationalOrchestration.Contracts](/Users/naveenkumarpatil/Documents/orchestrator%20design/backend/src/Framework/ConversationalOrchestration.Contracts)
- [ConversationalOrchestration.Application](/Users/naveenkumarpatil/Documents/orchestrator%20design/backend/src/Framework/ConversationalOrchestration.Application)
- [ConversationalOrchestration.Infrastructure](/Users/naveenkumarpatil/Documents/orchestrator%20design/backend/src/Framework/ConversationalOrchestration.Infrastructure)

Domain module:

- [ConversationalOrchestration.FundAdministration](/Users/naveenkumarpatil/Documents/orchestrator%20design/backend/src/Domain/ConversationalOrchestration.FundAdministration)

Hosts:

- [ConversationalOrchestration.Api](/Users/naveenkumarpatil/Documents/orchestrator%20design/backend/src/Hosts/ConversationalOrchestration.Api)
- [ConversationalOrchestration.Worker](/Users/naveenkumarpatil/Documents/orchestrator%20design/backend/src/Hosts/ConversationalOrchestration.Worker)

Frontend:

- [frontend/app](/Users/naveenkumarpatil/Documents/orchestrator%20design/frontend/app)

## Current Runtime Model

The framework separates:

- `Conversation`: the chat thread the user sees
- `Operation`: a single unit of work in that thread
- `ReviewTask`: a human approval or edit-and-submit task tied to an operation
- `WorkflowInstance`: the long-running execution state
- `WorkflowCheckpoint`: resumable workflow snapshot

One conversation can contain multiple operations at the same time. Incoming messages are routed to one of:

- start a new operation
- continue an existing operation
- respond to a review task
- ask for status
- ask for clarification when routing is ambiguous

## Workflow Engine

The project includes a workflow abstraction layer that wraps Microsoft Agent Framework Workflows:

- `IWorkflowRuntimeService`
- `IWorkflowRegistry`
- `IWorkflowDefinition`

Current implementation:

- [WorkflowRuntime.cs](/Users/naveenkumarpatil/Documents/orchestrator%20design/backend/src/Framework/ConversationalOrchestration.Infrastructure/Workflows/WorkflowRuntime.cs)
- [MongoJsonCheckpointStore.cs](/Users/naveenkumarpatil/Documents/orchestrator%20design/backend/src/Framework/ConversationalOrchestration.Infrastructure/Workflows/MongoJsonCheckpointStore.cs)

Current workflow definitions:

- [CapitalCallNoticeWorkflowDefinition.cs](/Users/naveenkumarpatil/Documents/orchestrator%20design/backend/src/Domain/ConversationalOrchestration.FundAdministration/Workflows/CapitalCallNoticeWorkflowDefinition.cs) - code-first graph built with `WorkflowBuilder`
- [FundOnboardingWorkflowDefinition.cs](/Users/naveenkumarpatil/Documents/orchestrator%20design/backend/src/Domain/ConversationalOrchestration.FundAdministration/Workflows/FundOnboardingWorkflowDefinition.cs)

Notes:

- workflow state is checkpointed in MongoDB
- workflows can pause on external input / human review
- workflows can resume from persisted checkpoints
- the app keeps its own business state in Mongo alongside workflow state

## LLM Layer

The app uses a provider-neutral LLM abstraction in application code:

- [StructuredLlm.cs](/Users/naveenkumarpatil/Documents/orchestrator%20design/backend/src/Framework/ConversationalOrchestration.Application/Abstractions/StructuredLlm.cs)

Current infrastructure implementation:

- [MicrosoftExtensionsAiStructuredLlmClient.cs](/Users/naveenkumarpatil/Documents/orchestrator%20design/backend/src/Framework/ConversationalOrchestration.Infrastructure/AI/MicrosoftExtensionsAiStructuredLlmClient.cs)

Current behavior:

- intent routing uses an LLM-backed classifier
- capital-call chat intake and clarification interpretation use structured LLM extraction
- conversation history compaction is used before LLM extraction
- typed structured output is used instead of raw JSON-string parsing

## Capital Call Notice Flow

The capital call flow is the most complete end-to-end workflow in the sample.

Input modes:

- SaaS-backed fund data
- attachment-driven CSV data
- future MCP provider/sink hook is modeled but not implemented as a real integration yet

Behavior:

- user can start with partial input like `Create a capital call notice for Apex Fund I`
- the system keeps asking in the same thread until mandatory inputs are complete
- once ready, the workflow first builds extracted partner and feeder data for review
- the reviewer can edit the extracted payload and submit it back into the workflow
- the reviewer can either describe changes in natural language or use the advanced raw payload editor
- only after review submission does the workflow compute fund and feeder allocations
- FX conversion happens at feeder-fund boundaries
- partner allocations are rounded to 2 decimals
- residual cents are settled to the last partner after deterministic sorting
- the approved allocation data is then handed off to a separate reusable `Template Output` operation
- generated review workbooks and template outputs are stored separately from uploaded source files in the conversation snapshot

Relevant implementation:

- [NoticeCreationAgent.cs](/Users/naveenkumarpatil/Documents/orchestrator%20design/backend/src/Domain/ConversationalOrchestration.FundAdministration/Agents/NoticeCreationAgent.cs)
- [TemplateOutputAgent.cs](/Users/naveenkumarpatil/Documents/orchestrator%20design/backend/src/Domain/ConversationalOrchestration.FundAdministration/Agents/TemplateOutputAgent.cs)
- [CapitalCallConversationIntelligence.cs](/Users/naveenkumarpatil/Documents/orchestrator%20design/backend/src/Domain/ConversationalOrchestration.FundAdministration/CapitalCalls/CapitalCallConversationIntelligence.cs)
- [CapitalCallExecutionServices.cs](/Users/naveenkumarpatil/Documents/orchestrator%20design/backend/src/Domain/ConversationalOrchestration.FundAdministration/CapitalCalls/CapitalCallExecutionServices.cs)
- [FundAdministrationWorkflowDispatcher.cs](/Users/naveenkumarpatil/Documents/orchestrator%20design/backend/src/Domain/ConversationalOrchestration.FundAdministration/Workflows/FundAdministrationWorkflowDispatcher.cs)
- [ReviewTaskService.cs](/Users/naveenkumarpatil/Documents/orchestrator%20design/backend/src/Framework/ConversationalOrchestration.Application/Reviews/ReviewTaskService.cs)
- [LlmReviewPayloadRevisionService.cs](/Users/naveenkumarpatil/Documents/orchestrator%20design/backend/src/Framework/ConversationalOrchestration.Infrastructure/AI/LlmReviewPayloadRevisionService.cs)
- [docs/capital-call-notice-flow.md](/Users/naveenkumarpatil/Documents/orchestrator%20design/docs/capital-call-notice-flow.md)

## Fund Onboarding Flow

The onboarding flow demonstrates:

- long-running workflow orchestration
- external-processing style checkpoints
- review queue integration
- workflow pause and resume
- persisted operation state plus persisted workflow state

Review completion resumes the workflow instead of restarting the original request.

## Logging and Traceability

The backend now includes structured logging in the main orchestration path so failures and state transitions are easier to track.

Current logging coverage includes:

- chat intake and routing decisions
- operation start and continue paths
- capital-call clarification loops
- request preparation and validation failures
- workflow start, resume, retry, pending input, checkpoint persistence, and workflow errors
- feeder expansion and FX conversion
- final result materialization into drafts or artifacts

Key files with logging:

- [ChatOrchestratorService.cs](/Users/naveenkumarpatil/Documents/orchestrator%20design/backend/src/Framework/ConversationalOrchestration.Application/Conversations/ChatOrchestratorService.cs)
- [FundAdministrationWorkflowDispatcher.cs](/Users/naveenkumarpatil/Documents/orchestrator%20design/backend/src/Domain/ConversationalOrchestration.FundAdministration/Workflows/FundAdministrationWorkflowDispatcher.cs)
- [CapitalCallConversationIntelligence.cs](/Users/naveenkumarpatil/Documents/orchestrator%20design/backend/src/Domain/ConversationalOrchestration.FundAdministration/CapitalCalls/CapitalCallConversationIntelligence.cs)
- [CapitalCallExecutionServices.cs](/Users/naveenkumarpatil/Documents/orchestrator%20design/backend/src/Domain/ConversationalOrchestration.FundAdministration/CapitalCalls/CapitalCallExecutionServices.cs)
- [WorkflowRuntime.cs](/Users/naveenkumarpatil/Documents/orchestrator%20design/backend/src/Framework/ConversationalOrchestration.Infrastructure/Workflows/WorkflowRuntime.cs)

## Local Setup

### 1. Start infrastructure

```bash
docker compose up -d
```

Infra currently started by compose:

- MongoDB on `mongodb://localhost:27018`

Compose file:

- [docker-compose.yml](/Users/naveenkumarpatil/Documents/orchestrator%20design/docker-compose.yml)

### 2. Run the API

```bash
cd "/Users/naveenkumarpatil/Documents/orchestrator design/backend"
$HOME/.dotnet/dotnet run --project src/Hosts/ConversationalOrchestration.Api
```

Default local API URL:

- `http://localhost:8080`

Launch settings:

- [launchSettings.json](/Users/naveenkumarpatil/Documents/orchestrator%20design/backend/src/Hosts/ConversationalOrchestration.Api/Properties/launchSettings.json)

### 3. Run the worker host

The worker host is available, but the current sample runs most user-facing orchestration through the API/runtime path. You can still start it if you want parity with the host structure.

```bash
cd "/Users/naveenkumarpatil/Documents/orchestrator design/backend"
$HOME/.dotnet/dotnet run --project src/Hosts/ConversationalOrchestration.Worker
```

### 4. Run the frontend

```bash
cd "/Users/naveenkumarpatil/Documents/orchestrator design/frontend/app"
npm install
npm run dev
```

Frontend URL:

- `http://localhost:5173`

The Vite dev server proxies `/api` to `http://localhost:8080`.

Frontend config:

- [vite.config.ts](/Users/naveenkumarpatil/Documents/orchestrator%20design/frontend/app/vite.config.ts)

## Configuration

API settings live in:

- [appsettings.json](/Users/naveenkumarpatil/Documents/orchestrator%20design/backend/src/Hosts/ConversationalOrchestration.Api/appsettings.json)
- [appsettings.Development.json](/Users/naveenkumarpatil/Documents/orchestrator%20design/backend/src/Hosts/ConversationalOrchestration.Api/appsettings.Development.json)

Important sections:

- `Mongo`
- `Storage`
- `LlmGateway`
- `Logging`

Recommended `LlmGateway` settings:

- `Provider`: `OpenAICompatible`
- `BaseUrl`: `https://api.openai.com/v1`
- `RoutingModel`: current lightweight model for routing
- `InputCompletionModel`: current lightweight model for structured slot filling

Important:

- do not keep real API keys in source-controlled `appsettings`
- prefer environment variables or user secrets for `LlmGateway:ApiKey`

Example:

```bash
export LlmGateway__ApiKey="your-key"
```

## Demo Flows

### Capital Call Notice

1. Start the app.
2. Send: `Create a capital call notice for Apex Fund I for INR 1000`
3. The system should compute allocations and return a draft notice action in SaaS mode.

Clarification loop example:

1. Send: `Create a capital call notice for Apex Fund I`
2. The system asks for the missing amount.
3. Send: `Raise INR 1000`
4. The same operation continues and completes.

Attachment mode example:

1. Upload a CSV with partner commitment data.
2. Send: `Create a capital call notice for Apex Fund I for INR 1000`
3. The system uses the attachment-backed profile and returns a downloadable CSV output.

### Fund Onboarding

1. Upload onboarding documents.
2. Send: `Create fund from these documents`
3. Wait for review tasks to appear.
4. Approve the review task from the UI or by chat.

### Multiple Operations in One Conversation

1. Start onboarding.
2. Before it completes, ask for a one-pager or capital call.
3. The same conversation keeps multiple operations alive at once.
4. The chat thread stays continuous while the operation state remains isolated.

## Build Verification

Backend:

```bash
$HOME/.dotnet/dotnet build backend/ConversationalOrchestration.sln
```

Frontend:

```bash
cd frontend/app
npm run build
```

## Current Limitations

- the workflow abstraction still wraps Microsoft workflow concepts rather than fully hiding them
- MCP is modeled as an extension point, not a complete integration yet
- Excel input is represented today through attachment-driven file handling; flexible tenant-specific mapping is still a future improvement
- some sample data providers and FX rates are in-memory/demo implementations
- there are no automated tests yet by design for this prototype-focused repo

## Useful Entry Points

If you want to explore the codebase quickly, start here:

- [Program.cs](/Users/naveenkumarpatil/Documents/orchestrator%20design/backend/src/Hosts/ConversationalOrchestration.Api/Program.cs)
- [ServiceCollectionExtensions.cs](/Users/naveenkumarpatil/Documents/orchestrator%20design/backend/src/Framework/ConversationalOrchestration.Infrastructure/Extensions/ServiceCollectionExtensions.cs)
- [FundAdministrationServiceCollectionExtensions.cs](/Users/naveenkumarpatil/Documents/orchestrator%20design/backend/src/Domain/ConversationalOrchestration.FundAdministration/Extensions/FundAdministrationServiceCollectionExtensions.cs)
- [ChatOrchestratorService.cs](/Users/naveenkumarpatil/Documents/orchestrator%20design/backend/src/Framework/ConversationalOrchestration.Application/Conversations/ChatOrchestratorService.cs)
- [MessageRoutingService.cs](/Users/naveenkumarpatil/Documents/orchestrator%20design/backend/src/Framework/ConversationalOrchestration.Application/Operations/MessageRoutingService.cs)
