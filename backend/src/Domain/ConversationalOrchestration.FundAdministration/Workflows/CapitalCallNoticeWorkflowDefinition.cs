using ConversationalOrchestration.Application.Abstractions;
using ConversationalOrchestration.Application.Support;
using ConversationalOrchestration.FundAdministration.CapitalCalls;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Declarative.Events;
using Microsoft.Extensions.DependencyInjection;

namespace ConversationalOrchestration.FundAdministration.Workflows;

public static partial class FundAdministrationWorkflowNames
{
    public const string CapitalCallNotice = "capital-call-notice";
}

public static class CapitalCallNoticeWorkflowPorts
{
    public const string AllocationReview = "capital-call-allocation-review";
}

public sealed record CapitalCallAllocationReviewPayload(
    string TenantId,
    string ConversationId,
    string OperationId,
    string RequestStateJson,
    CapitalCallNoticeDto Notice);

public sealed class CapitalCallNoticeWorkflowDefinition : IWorkflowDefinition
{
    private static readonly RequestPort<CapitalCallAllocationReviewPayload, CapitalCallAllocationReviewPayload> AllocationReviewPort =
        RequestPort.Create<CapitalCallAllocationReviewPayload, CapitalCallAllocationReviewPayload>(CapitalCallNoticeWorkflowPorts.AllocationReview);

    private readonly IServiceScopeFactory _serviceScopeFactory;

    public CapitalCallNoticeWorkflowDefinition(IServiceScopeFactory serviceScopeFactory)
    {
        _serviceScopeFactory = serviceScopeFactory;
    }

    public string Name => FundAdministrationWorkflowNames.CapitalCallNotice;

    public Type StartInputType => typeof(CapitalCallWorkflowStart);

    public Workflow Build(WorkflowBuildContext buildContext)
    {
        // This workflow covers the deterministic execution phase after chat intake has already
        // collected the required request inputs.
        // User clarification happens before the workflow starts.
        var loadRequestState = new LoadRequestStateExecutor().BindExecutor();
        var computeAllocations = new ComputeCapitalCallAllocationsExecutor(_serviceScopeFactory).BindExecutor();
        var allocationReview = AllocationReviewPort.BindAsExecutor();
        var materializeResult = new MaterializeCapitalCallResultExecutor(_serviceScopeFactory).BindExecutor();

        // Graph shape:
        // start -> load request state -> compute allocations -> human review -> materialize result -> output
        return new WorkflowBuilder(loadRequestState)
            .WithName("Capital Call Notice")
            .WithDescription("Loads request state, computes capital call allocations, pauses for human review, and materializes the final Excel output.")
            .AddEdge(loadRequestState, computeAllocations, "load -> compute", idempotent: true)
            .AddEdge(computeAllocations, allocationReview, "compute -> review", idempotent: true)
            .AddEdge(allocationReview, materializeResult, "review -> materialize", idempotent: true)
            .WithOutputFrom(materializeResult)
            .Build(validateOrphans: true);
    }

    public RequestPortDescriptor ResolveRequestPort(string portId) =>
        portId switch
        {
            CapitalCallNoticeWorkflowPorts.AllocationReview => new RequestPortDescriptor(
                CapitalCallNoticeWorkflowPorts.AllocationReview,
                typeof(CapitalCallAllocationReviewPayload),
                typeof(CapitalCallAllocationReviewPayload),
                AllocationReviewPort),
            _ => throw new InvalidOperationException(
                $"Workflow '{Name}' does not define request port '{portId}'. Capital call clarification currently happens before the workflow starts.")
        };

    public Task<ExternalResponse?> TryCreateAutomaticResponseAsync(
        RequestInfoEvent requestInfoEvent,
        CancellationToken cancellationToken) =>
        Task.FromResult<ExternalResponse?>(null);

    // Normalizes the workflow start payload into the compact state object used by the rest of
    // the graph. In practice this means: prefer the prebuilt RequestStateJson; otherwise fall
    // back to the raw initial message payload.
    private sealed class LoadRequestStateExecutor : Executor<CapitalCallWorkflowStart, CapitalCallWorkflowRequestState>
    {
        public LoadRequestStateExecutor()
            : base("load_request_state")
        {
        }

        public override ValueTask<CapitalCallWorkflowRequestState> HandleAsync(
            CapitalCallWorkflowStart input,
            IWorkflowContext context,
            CancellationToken cancellationToken)
        {
            var requestStateJson = string.IsNullOrWhiteSpace(input.RequestStateJson)
                ? input.InitialUserMessage
                : input.RequestStateJson;

            return ValueTask.FromResult(new CapitalCallWorkflowRequestState(
                input.TenantId,
                input.ConversationId,
                input.OperationId,
                requestStateJson));
        }
    }

    // Executes the deterministic allocation engine and packages the computed notice DTO into the
    // review payload that is shown to the human approver before any output artifact is created.
    private sealed class ComputeCapitalCallAllocationsExecutor : Executor<CapitalCallWorkflowRequestState, CapitalCallAllocationReviewPayload>
    {
        private readonly IServiceScopeFactory _serviceScopeFactory;

        public ComputeCapitalCallAllocationsExecutor(IServiceScopeFactory serviceScopeFactory)
            : base("compute_allocations")
        {
            _serviceScopeFactory = serviceScopeFactory;
        }

        public override async ValueTask<CapitalCallAllocationReviewPayload> HandleAsync(
            CapitalCallWorkflowRequestState input,
            IWorkflowContext context,
            CancellationToken cancellationToken)
        {
            await using var scope = _serviceScopeFactory.CreateAsyncScope();
            var engine = scope.ServiceProvider.GetRequiredService<ICapitalCallAllocationEngine>();
            var result = await engine.ComputeAsync(
                input.TenantId,
                input.ConversationId,
                input.RequestStateJson,
                cancellationToken);

            return new CapitalCallAllocationReviewPayload(
                input.TenantId,
                input.ConversationId,
                input.OperationId,
                input.RequestStateJson,
                result.Notice);
        }
    }

    // Persists the final user-facing result only after the human has approved the reviewed
    // allocation payload. The current implementation materializes to an Excel artifact.
    private sealed class MaterializeCapitalCallResultExecutor : Executor<CapitalCallAllocationReviewPayload, CapitalCallMaterializationResult>
    {
        private readonly IServiceScopeFactory _serviceScopeFactory;

        public MaterializeCapitalCallResultExecutor(IServiceScopeFactory serviceScopeFactory)
            : base("materialize_result")
        {
            _serviceScopeFactory = serviceScopeFactory;
        }

        public override async ValueTask<CapitalCallMaterializationResult> HandleAsync(
            CapitalCallAllocationReviewPayload input,
            IWorkflowContext context,
            CancellationToken cancellationToken)
        {
            await using var scope = _serviceScopeFactory.CreateAsyncScope();
            var materializer = scope.ServiceProvider.GetRequiredService<ICapitalCallResultMaterializer>();
            return await materializer.MaterializeAsync(
                input.TenantId,
                input.ConversationId,
                input.OperationId,
                input.RequestStateJson,
                JsonContent.Serialize(input.Notice),
                cancellationToken);
        }
    }

    // Shared state after loading request data and before running the allocation engine.
    private sealed record CapitalCallWorkflowRequestState(
        string TenantId,
        string ConversationId,
        string OperationId,
        string RequestStateJson);
}
