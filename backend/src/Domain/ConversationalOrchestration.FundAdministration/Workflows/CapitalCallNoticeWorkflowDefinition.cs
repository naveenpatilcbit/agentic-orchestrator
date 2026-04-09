using System.IO;
using ConversationalOrchestration.Application.Abstractions;
using ConversationalOrchestration.FundAdministration.CapitalCalls;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Declarative;
using Microsoft.Agents.AI.Workflows.Declarative.Events;
using Microsoft.Extensions.AI;
using System.Text.Json;

namespace ConversationalOrchestration.FundAdministration.Workflows;

public static partial class FundAdministrationWorkflowNames
{
    public const string CapitalCallNotice = "capital-call-notice";
}

public static class CapitalCallWorkflowPorts
{
    public const string ClarificationInput = "request_capital_call_clarification_Input";
}

public sealed class CapitalCallNoticeWorkflowDefinition : IWorkflowDefinition
{
    private const string WorkflowYaml =
        """
        kind: Workflow
        trigger:
          kind: OnConversationStart
          id: capital_call_notice_flow
          actions:
            - kind: SetVariable
              id: load_request_state
              variable: Local.CurrentStateJson
              value: =System.LastMessage.Text

            - kind: InvokeFunctionTool
              id: compute_allocations
              functionName: computeCapitalCallAllocations
              arguments:
                tenantId: tenant-demo
                conversationId: =System.ConversationId
                requestStateJson: =Local.CurrentStateJson
              output:
                result: Local.ComputationResult

            - kind: InvokeFunctionTool
              id: materialize_result
              functionName: materializeCapitalCallResult
              arguments:
                tenantId: tenant-demo
                conversationId: =System.ConversationId
                requestStateJson: =Local.CurrentStateJson
                noticeDtoJson: =Local.ComputationResult.noticeDtoJson
              output:
                result: Local.MaterializedResult
        """;

    private readonly CapitalCallWorkflowResponseAgentProvider _agentProvider;

    public CapitalCallNoticeWorkflowDefinition(CapitalCallWorkflowResponseAgentProvider agentProvider)
    {
        _agentProvider = agentProvider;
    }

    public string Name => FundAdministrationWorkflowNames.CapitalCallNotice;

    public Type StartInputType => typeof(CapitalCallWorkflowStart);

    public Workflow Build(WorkflowBuildContext buildContext)
    {
        var options = new DeclarativeWorkflowOptions(_agentProvider)
        {
            ConversationId = buildContext.ConversationId
        };

        using var reader = new StringReader(WorkflowYaml);
        return DeclarativeWorkflowBuilder.Build<CapitalCallWorkflowStart>(
            reader,
            options,
            input => new ChatMessage(
                ChatRole.User,
                string.IsNullOrWhiteSpace(input.RequestStateJson)
                    ? input.InitialUserMessage
                    : input.RequestStateJson));
    }

    public RequestPortDescriptor ResolveRequestPort(string portId)
    {
        if (!string.Equals(portId, CapitalCallWorkflowPorts.ClarificationInput, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Request port '{portId}' is not defined for workflow '{Name}'.");
        }

        var port = RequestPort.Create<ExternalInputRequest, ExternalInputResponse>(CapitalCallWorkflowPorts.ClarificationInput);
        return new RequestPortDescriptor(
            CapitalCallWorkflowPorts.ClarificationInput,
            typeof(ExternalInputRequest),
            typeof(ExternalInputResponse),
            port);
    }

    public async Task<ExternalResponse?> TryCreateAutomaticResponseAsync(
        RequestInfoEvent requestInfoEvent,
        CancellationToken cancellationToken)
    {
        if (!requestInfoEvent.Request.TryGetDataAs(typeof(ExternalInputRequest), out var requestValue) ||
            requestValue is not ExternalInputRequest externalInputRequest)
        {
            return null;
        }

        var functionCall = externalInputRequest.AgentResponse.Messages
            .SelectMany(message => message.Contents)
            .OfType<FunctionCallContent>()
            .FirstOrDefault(content => !string.IsNullOrWhiteSpace(content.Name));

        if (functionCall is null)
        {
            return null;
        }

        var function = (_agentProvider.Functions ?? Array.Empty<AIFunction>()).FirstOrDefault(candidate =>
            string.Equals(candidate.Name, functionCall.Name, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"Function '{functionCall.Name}' was requested by the declarative workflow but is not registered.");

        var arguments = new AIFunctionArguments(functionCall.Arguments);
        var functionResult = await function.InvokeAsync(arguments, cancellationToken);

        var port = RequestPort.Create<ExternalInputRequest, ExternalInputResponse>(requestInfoEvent.Request.PortInfo.PortId);
        var externalRequest = ExternalRequest.Create(port, externalInputRequest, requestInfoEvent.Request.RequestId);
        var responseMessage = new ChatMessage(
            ChatRole.Tool,
            [new FunctionResultContent(
                functionCall.CallId ?? requestInfoEvent.Request.RequestId,
                NormalizeFunctionResult(functionResult))]);

        return externalRequest.CreateResponse(new ExternalInputResponse(responseMessage));
    }

    private static object NormalizeFunctionResult(object? functionResult) =>
        functionResult switch
        {
            null => string.Empty,
            JsonElement element => NormalizeJsonElement(element),
            string or bool or byte or short or int or long or float or double or decimal => functionResult,
            _ => JsonSerializer.Serialize(functionResult)
        };

    private static object NormalizeJsonElement(JsonElement element) =>
        element.ValueKind switch
        {
            JsonValueKind.Object or JsonValueKind.Array => element.GetRawText(),
            JsonValueKind.String => element.GetString() ?? string.Empty,
            JsonValueKind.Number when element.TryGetInt64(out var int64Value) => int64Value,
            JsonValueKind.Number when element.TryGetDecimal(out var decimalValue) => decimalValue,
            JsonValueKind.Number when element.TryGetDouble(out var doubleValue) => doubleValue,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
            _ => element.GetRawText()
        };
}
