using ConversationalOrchestration.Application.Abstractions;
using Microsoft.Agents.AI.Workflows;

namespace ConversationalOrchestration.FundAdministration.Workflows;

public static partial class FundAdministrationWorkflowNames
{
    public const string FundOnboarding = "fund-onboarding";
}

public static class FundOnboardingWorkflowPorts
{
    public const string ClassificationReview = "classification-review";
    public const string ExtractionReview = "extraction-review";
}

public sealed record FundOnboardingWorkflowStart(
    string OperationId,
    string ConversationId,
    string InitiatedByUserId,
    IReadOnlyCollection<string> AttachmentIds,
    IReadOnlyCollection<string> AttachmentNames);

public sealed record ClassificationReviewPayload(
    IReadOnlyCollection<ClassificationDocumentPayload> Documents);

public sealed record ClassificationDocumentPayload(
    string FileName,
    string PredictedType);

public sealed record ExtractionReviewPayload(
    string FundName,
    string ManagementFee,
    string Domicile,
    string Currency);

public sealed record FundOnboardingWorkflowCompleted(
    string FundDraftRoute,
    string FundName,
    string ExtractionPayloadJson);

public sealed class FundOnboardingWorkflowDefinition : IWorkflowDefinition
{
    private static readonly RequestPort<ClassificationReviewPayload, ClassificationReviewPayload> ClassificationReviewPort =
        RequestPort.Create<ClassificationReviewPayload, ClassificationReviewPayload>(FundOnboardingWorkflowPorts.ClassificationReview);

    private static readonly RequestPort<ExtractionReviewPayload, ExtractionReviewPayload> ExtractionReviewPort =
        RequestPort.Create<ExtractionReviewPayload, ExtractionReviewPayload>(FundOnboardingWorkflowPorts.ExtractionReview);

    public string Name => FundAdministrationWorkflowNames.FundOnboarding;

    public Type StartInputType => typeof(FundOnboardingWorkflowStart);

    public object Build(WorkflowBuildContext buildContext)
    {
        Func<FundOnboardingWorkflowStart, ClassificationReviewPayload> intakeHandler = input =>
            new(
            [
                ..input.AttachmentNames.Select(name => new ClassificationDocumentPayload(
                    name,
                    InferDocumentType(name)))
            ]);

        var intake = intakeHandler.BindAsExecutor(id: "prepare-classification");

        var classificationReview = ClassificationReviewPort.BindAsExecutor();

        Func<ClassificationReviewPayload, ExtractionReviewPayload> prepareExtractionHandler = _ =>
            new(
                "Apex Growth Fund II",
                "1.75%",
                "Delaware",
                "USD");

        var prepareExtraction = prepareExtractionHandler.BindAsExecutor(id: "prepare-extraction");

        var extractionReview = ExtractionReviewPort.BindAsExecutor();

        Func<ExtractionReviewPayload, CancellationToken, ValueTask<FundOnboardingWorkflowCompleted>> finalizeDraftHandler =
            (review, _) => ValueTask.FromResult(new FundOnboardingWorkflowCompleted(
                $"/funds/drafts/{Slugify(review.FundName)}",
                review.FundName,
                System.Text.Json.JsonSerializer.Serialize(review)));

        var finalizeDraft = finalizeDraftHandler.BindAsExecutor(id: "create-draft");

        return new WorkflowBuilder(intake)
            .WithName("Fund Onboarding Workflow")
            .WithDescription("Collects document review checkpoints and produces a draft onboarding record.")
            .AddEdge(intake, classificationReview)
            .AddEdge(classificationReview, prepareExtraction)
            .AddEdge(prepareExtraction, extractionReview)
            .AddEdge(extractionReview, finalizeDraft)
            .WithOutputFrom(finalizeDraft)
            .Build(validateOrphans: true);
    }

    public RequestPortDescriptor ResolveRequestPort(string portId) =>
        portId switch
        {
            FundOnboardingWorkflowPorts.ClassificationReview => new RequestPortDescriptor(
                FundOnboardingWorkflowPorts.ClassificationReview,
                typeof(ClassificationReviewPayload),
                typeof(ClassificationReviewPayload),
                ClassificationReviewPort),
            FundOnboardingWorkflowPorts.ExtractionReview => new RequestPortDescriptor(
                FundOnboardingWorkflowPorts.ExtractionReview,
                typeof(ExtractionReviewPayload),
                typeof(ExtractionReviewPayload),
                ExtractionReviewPort),
            _ => throw new InvalidOperationException($"Request port '{portId}' is not defined for workflow '{Name}'.")
        };

    public Task<object?> TryCreateAutomaticResponseAsync(
        object requestInfoEvent,
        CancellationToken cancellationToken) =>
        Task.FromResult<object?>(null);

    private static string InferDocumentType(string fileName)
    {
        var normalized = fileName.ToLowerInvariant();
        if (normalized.Contains("subscription", StringComparison.Ordinal))
        {
            return "Subscription Document";
        }

        if (normalized.Contains("side", StringComparison.Ordinal))
        {
            return "Side Letter";
        }

        return "Limited Partnership Agreement";
    }

    private static string Slugify(string input)
    {
        var letters = input
            .ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character) ? character : '-')
            .ToArray();

        return string.Join(string.Empty, new string(letters).Split('-', StringSplitOptions.RemoveEmptyEntries));
    }
}
