using System.ComponentModel.DataAnnotations;

namespace CopilotPluginApi.Configuration;

/// <summary>
/// Represents configuration for prompt assembly.
/// </summary>
public sealed class PromptConfig
{
    private const int SystemPromptMaxLength = 8000;

    /// <summary>
    /// Gets the configuration section name.
    /// </summary>
    public const string SectionName = "Prompt";

    /// <summary>
    /// Gets or sets the system prompt prepended to every model invocation.
    /// </summary>
    [Required]
    [MaxLength(SystemPromptMaxLength)]
    public string SystemPrompt { get; set; } = string.Empty;
}
