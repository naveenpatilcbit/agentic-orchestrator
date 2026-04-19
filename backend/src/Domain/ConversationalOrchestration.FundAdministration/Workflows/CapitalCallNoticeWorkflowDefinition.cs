using ConversationalOrchestration.Application.Abstractions;
using ConversationalOrchestration.FundAdministration.CapitalCalls;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.DependencyInjection;

namespace ConversationalOrchestration.FundAdministration.Workflows;

public static partial class FundAdministrationWorkflowNames
{
    public const string CapitalCallNotice = "capital-call-notice";
}

public static class CapitalCallNoticeWorkflowPorts
{
    public const string ExtractionReview = "capital-call-extraction-review";
    public const string AllocationConfirmation = "capital-call-allocation-confirmation";
}

public sealed record CapitalCallWorkflowCompleted(
    CapitalCallExtractionReviewPayload ReviewedExtraction,
    CapitalCallNoticeDto Notice);

public sealed class CapitalCallNoticeWorkflowDefinition : IWorkflowDefinition
{
    private static readonly RequestPort<CapitalCallExtractionReviewPayload, CapitalCallExtractionReviewPayload> ExtractionReviewPort =
        RequestPort.Create<CapitalCallExtractionReviewPayload, CapitalCallExtractionReviewPayload>(CapitalCallNoticeWorkflowPorts.ExtractionReview);
    private static readonly RequestPort<CapitalCallWorkflowCompleted, CapitalCallWorkflowCompleted> AllocationConfirmationPort =
        RequestPort.Create<CapitalCallWorkflowCompleted, CapitalCallWorkflowCompleted>(CapitalCallNoticeWorkflowPorts.AllocationConfirmation);

    private readonly IServiceScopeFactory _serviceScopeFactory;

    public CapitalCallNoticeWorkflowDefinition(IServiceScopeFactory serviceScopeFactory)
    {
        _serviceScopeFactory = serviceScopeFactory;
    }

    public string Name => FundAdministrationWorkflowNames.CapitalCallNotice;

    public Type StartInputType => typeof(CapitalCallWorkflowStart);

    public object Build(WorkflowBuildContext buildContext)
    {
        // Chat intake already collected fund name, amount, and source resolution before this workflow starts.
        // Inside the workflow we first build the extracted partner/feeder tree, pause for human review and edits,
        // then run the deterministic allocation engine using the reviewed extraction payload and pause
        // once more so the user can confirm the final allocation output before the workflow completes.
        var loadRequestState = new LoadRequestStateExecutor().BindExecutor();
        var buildExtractionReview = new BuildCapitalCallExtractionReviewExecutor(_serviceScopeFactory).BindExecutor();
        var extractionReview = ExtractionReviewPort.BindAsExecutor();
        var computeAllocations = new ComputeCapitalCallAllocationsExecutor(_serviceScopeFactory).BindExecutor();
        var finalizeReviewedAllocations = new FinalizeApprovedCapitalCallExecutor().BindExecutor();
        var confirmFinalAllocations = AllocationConfirmationPort.BindAsExecutor();
        var publishConfirmedAllocations = new PublishConfirmedCapitalCallExecutor().BindExecutor();

        return new WorkflowBuilder(loadRequestState)
            .WithName("Capital Call Notice")
            .WithDescription("Builds extracted partner data, pauses for human review and edits, computes final capital call allocations, then asks for one last confirmation before downstream template operations.")
            .AddEdge(loadRequestState, buildExtractionReview, "load -> build review", idempotent: true)
            .AddEdge(buildExtractionReview, extractionReview, "build review -> human review", idempotent: true)
            .AddEdge(extractionReview, computeAllocations, "human review -> compute", idempotent: true)
            .AddEdge(computeAllocations, finalizeReviewedAllocations, "compute -> finalize", idempotent: true)
            .AddEdge(finalizeReviewedAllocations, confirmFinalAllocations, "finalize -> confirm", idempotent: true)
            .AddEdge(confirmFinalAllocations, publishConfirmedAllocations, "confirm -> publish", idempotent: true)
            .WithOutputFrom(publishConfirmedAllocations)
            .Build(validateOrphans: true);
    }

    public RequestPortDescriptor ResolveRequestPort(string portId) =>
        portId switch
        {
            CapitalCallNoticeWorkflowPorts.ExtractionReview => new RequestPortDescriptor(
                CapitalCallNoticeWorkflowPorts.ExtractionReview,
                typeof(CapitalCallExtractionReviewPayload),
                typeof(CapitalCallExtractionReviewPayload),
                ExtractionReviewPort),
            CapitalCallNoticeWorkflowPorts.AllocationConfirmation => new RequestPortDescriptor(
                CapitalCallNoticeWorkflowPorts.AllocationConfirmation,
                typeof(CapitalCallWorkflowCompleted),
                typeof(CapitalCallWorkflowCompleted),
                AllocationConfirmationPort),
            _ => throw new InvalidOperationException(
                $"Workflow '{Name}' does not define request port '{portId}'. Capital call clarification currently happens before the workflow starts.")
        };

    public Task<object?> TryCreateAutomaticResponseAsync(
        object requestInfoEvent,
        CancellationToken cancellationToken) =>
        Task.FromResult<object?>(null);

    // Normalizes the strongly typed workflow start payload into the compact state object used by
    // the rest of the graph. By the time the workflow starts, chat intake has already resolved the
    // canonical capital call request state.
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
            => ValueTask.FromResult(new CapitalCallWorkflowRequestState(
                input.TenantId,
                input.ConversationId,
                input.OperationId,
                input.RequestState));
    }

    // Builds the extracted partner/feeder tree that the reviewer can inspect and edit before any
    // allocation math is run. The review workbook is generated from this extraction payload.
    private sealed class BuildCapitalCallExtractionReviewExecutor : Executor<CapitalCallWorkflowRequestState, CapitalCallExtractionReviewPayload>
    {
        private readonly IServiceScopeFactory _serviceScopeFactory;

        public BuildCapitalCallExtractionReviewExecutor(IServiceScopeFactory serviceScopeFactory)
            : base("build_extraction_review")
        {
            _serviceScopeFactory = serviceScopeFactory;
        }

        public override async ValueTask<CapitalCallExtractionReviewPayload> HandleAsync(
            CapitalCallWorkflowRequestState input,
            IWorkflowContext context,
            CancellationToken cancellationToken)
        {
            await using var scope = _serviceScopeFactory.CreateAsyncScope();
            var extractionReviewService = scope.ServiceProvider.GetRequiredService<ICapitalCallExtractionReviewService>();
            var reviewArtifactService = scope.ServiceProvider.GetRequiredService<ICapitalCallReviewArtifactService>();
            var reviewPayload = await extractionReviewService.BuildAsync(
                input.TenantId,
                input.ConversationId,
                input.OperationId,
                input.RequestState,
                cancellationToken);
            var reviewArtifact = await reviewArtifactService.CreateAsync(
                input.TenantId,
                input.ConversationId,
                input.OperationId,
                reviewPayload,
                cancellationToken);

            reviewPayload.ReviewDownloadRoute = reviewArtifact.DownloadRoute;
            reviewPayload.ReviewFileName = reviewArtifact.FileName;
            return reviewPayload;
        }
    }

    // Runs the deterministic allocation engine only after the human-reviewed extraction payload is
    // available. That means suggested edits from the reviewer directly affect the math.
    private sealed class ComputeCapitalCallAllocationsExecutor : Executor<CapitalCallExtractionReviewPayload, CapitalCallComputedWorkflowState>
    {
        private readonly IServiceScopeFactory _serviceScopeFactory;

        public ComputeCapitalCallAllocationsExecutor(IServiceScopeFactory serviceScopeFactory)
            : base("compute_allocations")
        {
            _serviceScopeFactory = serviceScopeFactory;
        }

        public override async ValueTask<CapitalCallComputedWorkflowState> HandleAsync(
            CapitalCallExtractionReviewPayload input,
            IWorkflowContext context,
            CancellationToken cancellationToken)
        {
            await using var scope = _serviceScopeFactory.CreateAsyncScope();
            var engine = scope.ServiceProvider.GetRequiredService<ICapitalCallAllocationEngine>();
            var result = await engine.ComputeAsync(
                input.TenantId,
                input.ConversationId,
                input,
                cancellationToken);

            return new CapitalCallComputedWorkflowState(input, result);
        }
    }

    // Finalizes the workflow payload after review approval and deterministic allocation. A second
    // confirmation step still happens after this so the user can inspect the computed result.
    private sealed class FinalizeApprovedCapitalCallExecutor : Executor<CapitalCallComputedWorkflowState, CapitalCallWorkflowCompleted>
    {
        public FinalizeApprovedCapitalCallExecutor()
            : base("finalize_approved_data")
        {
        }

        public override ValueTask<CapitalCallWorkflowCompleted> HandleAsync(
            CapitalCallComputedWorkflowState input,
            IWorkflowContext context,
            CancellationToken cancellationToken)
            => ValueTask.FromResult(new CapitalCallWorkflowCompleted(
                input.ReviewPayload,
                input.ComputationResult.Notice));
    }

    // Publishes the already-confirmed workflow payload as the final workflow output.
    private sealed class PublishConfirmedCapitalCallExecutor : Executor<CapitalCallWorkflowCompleted, CapitalCallWorkflowCompleted>
    {
        public PublishConfirmedCapitalCallExecutor()
            : base("publish_confirmed_allocations")
        {
        }

        public override ValueTask<CapitalCallWorkflowCompleted> HandleAsync(
            CapitalCallWorkflowCompleted input,
            IWorkflowContext context,
            CancellationToken cancellationToken)
            => ValueTask.FromResult(input);
    }

    // Shared state after loading request data and before running the allocation engine.
    private sealed record CapitalCallWorkflowRequestState(
        string TenantId,
        string ConversationId,
        string OperationId,
        CapitalCallRequestState RequestState);

    private sealed record CapitalCallComputedWorkflowState(
        CapitalCallExtractionReviewPayload ReviewPayload,
        CapitalCallComputationResult ComputationResult);
}
