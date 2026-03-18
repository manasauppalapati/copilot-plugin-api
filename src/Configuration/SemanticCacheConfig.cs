using System.ComponentModel.DataAnnotations;

namespace CopilotPluginApi.Configuration;

/// <summary>
/// Represents configuration for semantic cache retention.
/// </summary>
public sealed class SemanticCacheConfig
{
    /// <summary>
    /// Gets the configuration section name.
    /// </summary>
    public const string SectionName = "SemanticCache";

    /// <summary>
    /// Gets or sets the semantic cache time-to-live in hours.
    /// </summary>
    [Range(1, 24)]
    public int TtlHours { get; set; }
}
