using ConversationalOrchestration.Application.Abstractions;
using ConversationalOrchestration.Application.Support;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace ConversationalOrchestration.FundAdministration.CapitalCalls;

public sealed class CapitalCallWorkflowTools
{
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly IReadOnlyCollection<AIFunction> _functions;

    public CapitalCallWorkflowTools(IServiceScopeFactory serviceScopeFactory)
    {
        _serviceScopeFactory = serviceScopeFactory;
        _functions =
        [
            AIFunctionFactory.Create(
                CaptureCapitalCallIntentAsync,
                "captureCapitalCallIntent",
                "Extracts initial capital call inputs from the operation conversation."),
            AIFunctionFactory.Create(
                InterpretCapitalCallClarificationAsync,
                "interpretCapitalCallClarification",
                "Extracts new values from the user's clarification message."),
            AIFunctionFactory.Create(
                PrepareCapitalCallRequestAsync,
                "prepareCapitalCallRequest",
                "Merges input patches with tenant data, resolves the execution profile, and validates the capital call request."),
            AIFunctionFactory.Create(
                ComputeCapitalCallAllocationsAsync,
                "computeCapitalCallAllocations",
                "Runs deterministic allocation, recursion, FX conversion, and rounding for the capital call."),
            AIFunctionFactory.Create(
                MaterializeCapitalCallResultAsync,
                "materializeCapitalCallResult",
                "Creates the SaaS draft notice or downloadable artifact and persists the operation result.")
        ];
    }

    public IReadOnlyCollection<AIFunction> Functions => _functions;

    public async Task<string> CaptureCapitalCallIntentAsync(
        string tenantId,
        string conversationId,
        string latestMessage,
        CancellationToken cancellationToken = default)
    {
        await using var scope = CreateAsyncScope();
        var operationId = await ResolveOperationIdAsync(scope.ServiceProvider, tenantId, conversationId, cancellationToken);
        var intelligence = scope.ServiceProvider.GetRequiredService<ICapitalCallConversationIntelligence>();
        var patch = await intelligence.CaptureIntentAsync(tenantId, conversationId, operationId, latestMessage, cancellationToken);
        return JsonContent.Serialize(patch);
    }

    public async Task<string> InterpretCapitalCallClarificationAsync(
        string tenantId,
        string conversationId,
        string responseText,
        CancellationToken cancellationToken = default)
    {
        await using var scope = CreateAsyncScope();
        var operationId = await ResolveOperationIdAsync(scope.ServiceProvider, tenantId, conversationId, cancellationToken);
        var intelligence = scope.ServiceProvider.GetRequiredService<ICapitalCallConversationIntelligence>();
        var patch = await intelligence.InterpretClarificationAsync(tenantId, conversationId, operationId, responseText, cancellationToken);
        return JsonContent.Serialize(patch);
    }

    public async Task<CapitalCallPreparationResult> PrepareCapitalCallRequestAsync(
        string tenantId,
        string conversationId,
        string currentStateJson,
        CapitalCallIntentPatch? patch,
        CancellationToken cancellationToken = default)
    {
        await using var scope = CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<ICapitalCallRequestPreparationService>();
        return await service.PrepareAsync(tenantId, conversationId, currentStateJson, patch, cancellationToken);
    }

    public async Task<CapitalCallComputationResult> ComputeCapitalCallAllocationsAsync(
        string tenantId,
        string conversationId,
        string requestStateJson,
        CancellationToken cancellationToken = default)
    {
        await using var scope = CreateAsyncScope();
        var engine = scope.ServiceProvider.GetRequiredService<ICapitalCallAllocationEngine>();
        return await engine.ComputeAsync(tenantId, conversationId, requestStateJson, cancellationToken);
    }

    public async Task<CapitalCallMaterializationResult> MaterializeCapitalCallResultAsync(
        string tenantId,
        string conversationId,
        string requestStateJson,
        string noticeDtoJson,
        CancellationToken cancellationToken = default)
    {
        await using var scope = CreateAsyncScope();
        var operationId = await ResolveOperationIdAsync(scope.ServiceProvider, tenantId, conversationId, cancellationToken);
        var materializer = scope.ServiceProvider.GetRequiredService<ICapitalCallResultMaterializer>();
        return await materializer.MaterializeAsync(tenantId, conversationId, operationId, requestStateJson, noticeDtoJson, cancellationToken);
    }

    private AsyncServiceScope CreateAsyncScope() => _serviceScopeFactory.CreateAsyncScope();

    private static async Task<string> ResolveOperationIdAsync(
        IServiceProvider serviceProvider,
        string tenantId,
        string conversationId,
        CancellationToken cancellationToken)
    {
        var conversationRepository = serviceProvider.GetRequiredService<IConversationRepository>();
        var conversation = await conversationRepository.GetAsync(conversationId, tenantId, cancellationToken)
            ?? throw new InvalidOperationException($"Conversation '{conversationId}' could not be found.");

        if (string.IsNullOrWhiteSpace(conversation.LastFocusedOperationId))
        {
            throw new InvalidOperationException($"Conversation '{conversationId}' does not have a focused operation for workflow execution.");
        }

        return conversation.LastFocusedOperationId;
    }
}
