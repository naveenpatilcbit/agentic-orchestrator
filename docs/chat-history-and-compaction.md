# Chat History And Compaction

This document explains how chat history is stored and reduced in the current `ConversationalOrchestration` implementation.

It covers:

- what is stored as raw conversation history
- what is stored as reduced history
- when compaction runs
- how full-thread compaction differs from operation-scoped compaction
- why plain `Microsoft.Extensions.AI.IChatClient` does not replace this layer

## 1. Why We Maintain Chat History Ourselves

This project treats chat history as product state, not just model input.

We need it for:

- rendering the chat thread in the UI
- loading previous conversations
- multi-operation routing inside one thread
- auditability
- workflow-driven system messages
- review-task continuity
- operation-scoped LLM extraction

Because of that, chat history is persisted in MongoDB even before any compaction happens.

## 2. Raw Conversation Model

### Conversation thread

Raw conversation metadata lives in:

- [ConversationModels.cs](/Users/naveenkumarpatil/Documents/orchestrator%20design/backend/src/Framework/ConversationalOrchestration.Domain/Conversations/ConversationModels.cs)

`ConversationThread` stores:

- `Id`
- `TenantId`
- `Title`
- `LastFocusedOperationId`
- `ReducedHistoryJson`
- `ReducedHistorySourceCount`
- `ReducedHistoryUpdatedAtUtc`
- timestamps

### Conversation messages

`ConversationMessage` stores each persisted message with:

- `ConversationId`
- optional `OperationId`
- `Role`
- `Content`
- `MessageKind`
- optional actions
- timestamp

This raw message list is the source of truth.

## 3. What “Compaction” Means Here

Compaction does **not** delete raw messages.

Instead, it produces a smaller derived message list for LLM use.

The service is:

- [ConversationHistoryCompactionService.cs](/Users/naveenkumarpatil/Documents/orchestrator%20design/backend/src/Framework/ConversationalOrchestration.Infrastructure/Conversations/ConversationHistoryCompactionService.cs)

The current strategy is:

1. load raw persisted messages
2. if message count is small enough, keep full history
3. if history is large, try summarization-based reduction
4. if summarization is unavailable, fall back to message-count reduction
5. persist reduced thread history on the conversation record when it is smaller than the original

## 4. Two Different Reduction Modes

The implementation intentionally has two scopes.

### A. Conversation-level reduced history

Used when we want a reduced representation of the entire thread.

Methods:

- `RefreshAsync(...)`
- `GetReducedConversationHistoryAsync(...)`

Current thresholds:

- threshold = `16`
- target = `12`

Behavior:

- if message count is `<= 16`, keep full history
- if message count is `> 16`, reduce toward `12`
- store the reduced result in `ConversationThread.ReducedHistoryJson`

This acts like a cached reduced view of the whole conversation.

### B. Operation-level reduced history

Used when an agent should look only at messages relevant to one operation.

Method:

- `GetReducedOperationHistoryAsync(...)`

Current thresholds:

- threshold = `12`
- target = `8`

Behavior:

- first filter raw messages by `OperationId`
- then reduce only that operation’s messages
- this reduced result is returned to the caller but is **not** stored separately on the operation

This is important because one conversation can contain multiple active operations at once.

## 5. Actual Reduction Algorithm

Inside `ReduceMessagesAsync(...)`, the service:

1. converts `ConversationMessage` records to `ChatMessage`
2. checks whether the message count exceeds the configured threshold
3. tries `SummarizingChatReducer`
4. if that is unavailable or returns null, uses `MessageCountingChatReducer`

Current reducer usage:

- `SummarizingChatReducer(chatClient, targetCount, thresholdCount)`
- `MessageCountingChatReducer(targetCount)`

The LLM used for summarization comes from:

- `ILlmChatClientFactory.TryGetChatClient(LlmProfile.InputCompletion)`

So the reducer is allowed to use a smaller or cheaper profile than the main generation model.

## 6. When Compaction Runs

### On every new user message

In:

- [ChatOrchestratorService.cs](/Users/naveenkumarpatil/Documents/orchestrator%20design/backend/src/Framework/ConversationalOrchestration.Application/Conversations/ChatOrchestratorService.cs)

Flow:

1. persist user message
2. call `RefreshAsync(tenantId, conversationId, ...)`
3. route the request

### After the user message is later attached to an operation

When routing decides which operation owns the message, the message may be updated with `OperationId`, and compaction is refreshed again so operation-scoped reduction sees the correct association.

### After assistant/system messages are appended

Also in `ChatOrchestratorService`, assistant responses refresh reduced history after being stored.

### After workflow/system updates

In:

- [FundAdministrationWorkflowDispatcher.cs](/Users/naveenkumarpatil/Documents/orchestrator%20design/backend/src/Domain/ConversationalOrchestration.FundAdministration/Workflows/FundAdministrationWorkflowDispatcher.cs)

Workflow-generated system messages also call `RefreshAsync(...)`.

This matters because review-ready notices, workflow failures, and checkpoint updates all become part of the thread context.

## 7. Where Reduced History Is Actually Used

### Capital call intake and clarification

In:

- [CapitalCallConversationIntelligence.cs](/Users/naveenkumarpatil/Documents/orchestrator%20design/backend/src/Domain/ConversationalOrchestration.FundAdministration/CapitalCalls/CapitalCallConversationIntelligence.cs)

The capital call extractor uses:

- `GetReducedOperationHistoryAsync(...)`

That means capital call slot-filling does **not** inspect the full chat thread. It only sees the reduced history for that one operation.

This is an important safety choice because one conversation can contain multiple operations.

### Generic input completion

The LLM input-completion service in:

- [LlmAgentInputCompletionService.cs](/Users/naveenkumarpatil/Documents/orchestrator%20design/backend/src/Framework/ConversationalOrchestration.Infrastructure/AI/LlmAgentInputCompletionService.cs)

does not directly call the compaction service itself. Instead, it receives already-scoped relevant conversation history from the orchestrator/agent layer.

### Routing

The routing classifier in:

- [LlmMessageIntentClassifier.cs](/Users/naveenkumarpatil/Documents/orchestrator%20design/backend/src/Framework/ConversationalOrchestration.Infrastructure/AI/LlmMessageIntentClassifier.cs)

does not currently use reduced message history. It routes based on:

- current message
- conversation metadata
- operations
- review tasks
- files
- available agents

## 8. Why We Cache Reduced Conversation History

`ConversationThread.ReducedHistoryJson` is a lightweight cache.

We store it only when:

- reduced history exists
- and reduced count is smaller than original count

We also store:

- `ReducedHistorySourceCount`

That lets us cheaply know whether the cached reduced history is still valid for the current number of raw messages.

If the raw message count changed, the next read recomputes the reduced history.

## 9. Why Plain Microsoft Chat Client Does Not Handle This For Us

Short answer: **not by itself**.

With plain `Microsoft.Extensions.AI.IChatClient`, the caller is still expected to manage the message list.

Microsoft’s own docs show the caller maintaining `List<ChatMessage>` and passing it back on every call:

- [Use the IChatClient interface](https://learn.microsoft.com/en-us/dotnet/ai/advanced/sample-implementations)
- [Chat quickstart](https://learn.microsoft.com/en-us/dotnet/ai/quickstarts/chat-local-model)

That is enough for a simple single-thread console chat, but not for this product.

We still need our own history layer because this app has:

- persisted conversations in Mongo
- previous-thread loading
- multiple operations in one conversation
- workflow/system messages
- review-task continuity
- audit/event projection
- operation-scoped context selection

`IChatClient` is the model-call abstraction. It is not the product conversation-state manager.

## 10. What Microsoft Can Help With

There are two useful Microsoft features here.

### A. `Microsoft.Extensions.AI` chat reducers

This is what the current code uses.

Examples:

- `SummarizingChatReducer`
- `MessageCountingChatReducer`

Official docs:

- [SummarizingChatReducer](https://learn.microsoft.com/en-us/dotnet/api/microsoft.extensions.ai.summarizingchatreducer)
- [MessageCountingChatReducer](https://learn.microsoft.com/en-us/dotnet/api/microsoft.extensions.ai.messagecountingchatreducer)

These help reduce a message list, but they do not persist product conversation state for us.

### B. Microsoft Agent Framework history + compaction providers

If we were using `ChatClientAgent` sessions as the main conversation model, Microsoft Agent Framework offers:

- `ChatHistoryProvider`
- `CompactionProvider`

Official docs:

- [Agent Framework conversation compaction](https://learn.microsoft.com/en-us/agent-framework/agents/conversations/compaction)
- [Third-party chat history storage](https://learn.microsoft.com/en-us/agent-framework/tutorials/agents/third-party-chat-history-storage)

Those are useful for agent-session history management, but they still do not fully replace this repo’s custom conversation model because we also need:

- UI snapshots
- operation correlation
- review queue correlation
- auditability
- cross-agent orchestration state

So the answer is:

- `IChatClient` alone: no
- `Agent Framework ChatHistoryProvider + CompactionProvider`: helpful for agent-session memory, but still not a full substitute for our product-level chat and operation persistence

## 11. Recommended Mental Model

Use this split:

- `Mongo conversation/messages` = source of truth for product chat history
- `ConversationHistoryCompactionService` = derived reduced context for LLM use
- `IChatClient` = model invocation abstraction
- `Agent Framework history/compaction providers` = optional agent-session helpers, not the product state model

That is why the current design keeps compaction as an application service rather than outsourcing the entire concern to the chat client.
