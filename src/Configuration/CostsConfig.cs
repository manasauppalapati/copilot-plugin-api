using System.ComponentModel.DataAnnotations;

namespace CopilotPluginApi.Configuration;

/// <summary>
/// Represents configuration for model pricing used in cost estimation.
/// </summary>
public sealed class CostsConfig
{
    /// <summary>
    /// Gets the configuration section name.
    /// </summary>
    public const string SectionName = "Costs";

    /// <summary>
    /// Gets or sets the primary deployment input price per thousand prompt tokens in USD.
    /// </summary>
    [Range(0D, double.MaxValue)]
    public double PrimaryInputPerThousand { get; set; }

    /// <summary>
    /// Gets or sets the primary deployment output price per thousand completion tokens in USD.
    /// </summary>
    [Range(0D, double.MaxValue)]
    public double PrimaryOutputPerThousand { get; set; }

    /// <summary>
    /// Gets or sets the fallback deployment input price per thousand prompt tokens in USD.
    /// </summary>
    [Range(0D, double.MaxValue)]
    public double FallbackInputPerThousand { get; set; }

    /// <summary>
    /// Gets or sets the fallback deployment output price per thousand completion tokens in USD.
    /// </summary>
    [Range(0D, double.MaxValue)]
    public double FallbackOutputPerThousand { get; set; }
}
