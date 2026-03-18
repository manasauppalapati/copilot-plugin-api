namespace CopilotPluginApi.Models;

/// <summary>
/// The exception that is thrown when a prompt cannot be assembled within the configured token budget.
/// </summary>
public sealed class PromptTooLargeException : InvalidOperationException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PromptTooLargeException" /> class.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    public PromptTooLargeException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="PromptTooLargeException" /> class.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    /// <param name="innerException">The exception that caused the current exception.</param>
    public PromptTooLargeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
