# Workflow Chaining Strategies (Enterprise Guidance)

This doc compares two approaches for handling a **single user prompt that requires multiple workflows/agents executed in order** (A then B then C).

The two options:

- **Option 1: Plan + Executor (application-managed execution)**
- **Option 2: Tool-Driven Execution (Agent Framework invokes workflows as tools)**

The recommendation for enterprise automation is typically **Option 1**, with a possible hybrid where the agent can use safe read-only tools while the app remains the executor for write/side-effecting actions.

## Option 1: Plan + Executor (Recommended)

### Summary

Use an LLM to produce an explicit, structured **plan** (a queue of steps). Persist the plan on the operation. Execute steps deterministically in the application. Pause/resume on review tasks.

### What This Looks Like

1. Router/Planner produces a plan:
   - Step 1: run workflow/agent A
   - Step 2: run workflow/agent B using Step 1 outputs
   - Step 3: run workflow/agent C using Step 2 outputs
2. Persist the plan (and step statuses) in Mongo as part of the operation state.
3. A `PlanExecutor` advances the plan:
   - Runs Step 1
   - If Step 1 completes, starts Step 2 automatically
   - If a step opens a review task, the plan pauses until the user approves/rejects, then resumes
4. Persist outputs from each step as first-class entities (e.g., `OperationOutput`) and reference them when executing the next step.

### Concrete Example

User prompt:

> "Create a capital call, then generate the notice PDF, then email it to LPs."

Plan (persisted):

- Step 1: `capital-call-notice` workflow
  - pauses at extraction review
  - pauses at allocation approval
- Step 2: `template-render` (requires Step 1 output id)
- Step 3: `send-email` (requires Step 2 output id + explicit user confirmation)

What you get:

- If the user logs out after Step 1 approval, the plan resumes exactly at Step 2.
- If email sending fails, you retry Step 3 only.
- If the client retries the same request, the executor can no-op completed steps.

### Why This Is Better for Enterprise

- **Determinism:** ordering is persisted and enforced (A → B → C) regardless of model variance.
- **Idempotency:** step-level “already completed” checks prevent duplicate side effects (emails, file generation, postings).
- **Pause/resume correctness:** review tasks become natural checkpoints in the plan.
- **Auditability:** a persisted plan explains *what was intended* and *what ran*, with timestamps, approvals, and output references.
- **Governance:** policies live outside the model (explicit approvals, allowlists, spend limits, business-hours constraints, etc.).
- **Model upgrades are safer:** changes mainly affect planning quality, not execution semantics.

### Costs

- More framework code: plan schema, plan persistence, step execution/resume logic.

## Option 2: Tool-Driven Execution (Agent Invokes Workflows as Tools)

### Summary

Expose workflow operations as tools (e.g., `StartWorkflow`, `ResumeWorkflow`, `SubmitReviewDecision`). The LLM decides which tool to call and in what order.

### Concrete Example Risk

Same user prompt:

> "Create a capital call, then generate the notice PDF, then email it to LPs."

Tool-driven execution can produce issues like:

- The model calls `StartCapitalCallWorkflow` twice if it retries after a timeout.
- The model tries to email before a human review step is approved unless tools enforce hard gating.
- If the agent run is interrupted and resumed, the model may re-run earlier tools unless you implement step-level state anyway.

### Why This Is Riskier for Enterprise

- **Ordering is probabilistic unless you add explicit state:** execution sequence depends on model behavior.
- **Idempotency burden is everywhere:** every workflow tool must be strongly idempotent and safe under retries/duplicates.
- **Harder debugging:** “why did it call that tool” becomes model-dependent unless you log and interpret tool call traces.
- **Larger safety surface:** tool arguments are untrusted; you must validate every argument and enforce tenant/user scoping in tools.

### When It Can Still Make Sense

- Read-only tools (status, lookup, list pending reviews).
- Low-risk tools with minimal side effects.
- A hybrid where the agent can propose and explain actions, but the executor enforces approvals and execution order.

## Recommendation

For enterprise-ready orchestration, prefer:

- **Option 1 (Plan + Executor)** for any multi-step automation that can:
  - create/modify records,
  - send emails/notifications,
  - move money or change allocations,
  - trigger downstream actions,
  - or requires review checkpoints.

You can layer in a hybrid:

- Agent framework is used for planning and slot-filling.
- App executes steps deterministically.
- Agent tools are limited to safe reads and proposal generation.

