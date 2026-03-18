using System.ComponentModel.DataAnnotations;

namespace CopilotPluginApi.Configuration;

/// <summary>
/// Represents configuration for Azure OpenAI connectivity and model routing.
/// </summary>
public sealed class AzureOpenAIConfig
{
    private const int EndpointMaxLength = 2048;
    private const int DeploymentNameMaxLength = 128;

    /// <summary>
    /// Gets the configuration section name.
    /// </summary>
    public const string SectionName = "AzureOpenAI";

    /// <summary>
    /// Gets or sets the Azure OpenAI endpoint URI.
    /// </summary>
    [Required]
    [Url]
    [MaxLength(EndpointMaxLength)]
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the Azure OpenAI API key.
    /// </summary>
    [Required]
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the primary deployment name used for normal traffic.
    /// </summary>
    [Required]
    [MaxLength(DeploymentNameMaxLength)]
    public string PrimaryDeployment { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the fallback deployment name used when the primary deployment is unhealthy.
    /// </summary>
    [Required]
    [MaxLength(DeploymentNameMaxLength)]
    public string FallbackDeployment { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the maximum number of transient retries for Azure OpenAI calls.
    /// </summary>
    [Range(0, 10)]
    public int MaxRetries { get; set; }
}
