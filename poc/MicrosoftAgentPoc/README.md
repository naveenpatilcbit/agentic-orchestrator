# Microsoft Agent Framework POC (Terminal)

This is a tiny terminal-only POC to learn **Microsoft.Agents.AI** tool orchestration:

- One **Master agent**
- One **workflow tool** with a **human-in-the-loop (HITL)** approval checkpoint (implemented using **Microsoft.Agents.AI.Workflows** `WorkflowBuilder` + `RequestPort`)
- One **specialist sub-agent** tool

The master agent can call either tool (or both) to answer your prompts.

## Run

```bash
cd "poc/MicrosoftAgentPoc"
export OPENAI_API_KEY="..."
export OPENAI_MODEL="gpt-4.1-mini"   # optional
export OPENAI_BASE_URL="..."         # optional (OpenAI-compatible gateway)

/Users/naveenkumarpatil/.dotnet/dotnet run
```

Commands:
- `/exit` quits
- `/reset` clears conversation + workflow state
- `/state` prints the workflow checkpoint state (debug)

## Try These Prompts

1. `Create a capital call notice for Apex Fund I for INR 1000.`
2. `APPROVE`
3. `Create a one-pager for Atlas Industrial in Board Presentation Format.`
4. `Do a capital call for Apex Fund I and then create a board one-pager for Atlas Industrial.`
