namespace ConversationalOrchestration.Application.Abstractions;

/// <summary>
/// Framework-owned chat turn model used at the Application boundary.
/// Infrastructure can map this to provider-specific chat types (e.g., Microsoft.Extensions.AI.ChatMessage).
/// </summary>
public sealed record ReducedChatMessage(
    ReducedChatRole Role,
    string Text);

public enum ReducedChatRole
{
    User = 1,
    Assistant = 2,
    System = 3
}

