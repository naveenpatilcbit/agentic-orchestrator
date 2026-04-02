# Fund Orchestrator Sample

Sample multi-agent fund administration orchestration app with:

- `.NET 9` clean-architecture backend
- `MongoDB` for conversations, operations, reviews, audit, and files
- `NServiceBus + RabbitMQ + MongoDB storage` for the onboarding saga
- `React + Vite` chat UI
- polling-based conversation refresh

## Local stack

Start infra:

```bash
docker compose up -d
```

Run the API:

```bash
cd backend
$HOME/.dotnet/dotnet run --project src/FundOrchestrator.Api
```

Run the workflow worker:

```bash
cd backend
$HOME/.dotnet/dotnet run --project src/FundOrchestrator.Worker
```

Run the frontend:

```bash
cd frontend/app
npm install
npm run dev
```

Frontend URL:

- `http://localhost:5173`

Backend URL:

- `http://localhost:8080`

RabbitMQ management UI:

- `http://localhost:15672`
- username: `guest`
- password: `guest`

MongoDB exposed port:

- `mongodb://localhost:27018`

## Demo scenarios

1. Upload onboarding documents in the chat panel.
2. Send: `Create fund from these documents`
3. While onboarding is waiting, send: `Generate a one-pager for BlueWave Systems`
4. Notice the conversation keeps one thread while the `Active Work` panel shows multiple operations.
5. When a review task appears, approve it from the right-side queue or by sending `Approve the extraction review`.

## Design notes reflected in the sample

- one conversation can contain many operations
- routing can continue an existing operation, answer a review task, ask for status, or start new work
- long-running onboarding is modeled as a saga
- financial or record-creating steps end in review gates or drafts
- chat is the user-facing projection, not the workflow source of truth
# agentic-orchestrator
