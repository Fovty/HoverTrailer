using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MediaBrowser.Model.Plugins;
using Fovty.Plugin.HoverTrailer.Exceptions;

namespace Fovty.Plugin.HoverTrailer.Configuration;

/// <summary>
/// Plugin configuration.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{

    /// <summary>
    /// Gets or sets a value indicating whether hover preview is enabled.
    /// </summary>
    public bool EnableHoverPreview { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the plugin should inject the client-side script tag into jellyfin-web.
    /// </summary>
    public bool InjectClientScript { get; set; } = true;

    /// <summary>
    /// Gets or sets the hover delay in milliseconds before starting preview.
    /// </summary>
    public int HoverDelayMs { get; set; } = 1000;

    /// <summary>
    /// Gets or sets the horizontal offset from default position in pixels.
    /// </summary>
    public int PreviewOffsetX { get; set; } = 0;

    /// <summary>
    /// Gets or sets the vertical offset from default position in pixels.
    /// </summary>
    public int PreviewOffsetY { get; set; } = 0;

    /// <summary>
    /// Gets or sets the preview sizing mode (Manual or FitContent).
    /// </summary>
    public string PreviewSizingMode { get; set; } = "FitContent";

    /// <summary>
    /// Gets or sets the preview positioning mode (Center or Custom).
    /// </summary>
    public string PreviewPositioningMode { get; set; } = "Center";

    /// <summary>
    /// Gets or sets the preview width in pixels (Manual mode).
    /// </summary>
    public int PreviewWidth { get; set; } = 300;

    /// <summary>
    /// Gets or sets the preview height in pixels (Manual mode).
    /// </summary>
    public int PreviewHeight { get; set; } = 200;

    /// <summary>
    /// Gets or sets the preview size percentage for FitContent mode scaling.
    /// </summary>
    public int PreviewSizePercentage { get; set; } = 200;

    /// <summary>
    /// Gets or sets the preview opacity (0.0 - 1.0).
    /// </summary>
    public double PreviewOpacity { get; set; } = 1.0;

    /// <summary>
    /// Gets or sets the preview border radius in pixels.
    /// </summary>
    public int PreviewBorderRadius { get; set; } = 10;

    /// <summary>
    /// Gets or sets whether to enable audio for trailer previews.
    /// </summary>
    public bool EnablePreviewAudio { get; set; } = true;

    /// <summary>
    /// Gets or sets the preview audio volume (0-100 percent).
    /// </summary>
    public int PreviewVolume { get; set; } = 50;

    /// <summary>
    /// Gets or sets the remote video quality for YouTube trailers.
    /// Valid values: "adaptive", "hd480", "hd720", "hd1080", "hd2160".
    /// </summary>
    public string RemoteVideoQuality { get; set; } = "adaptive";

    /// <summary>
    /// Gets or sets whether to enable background blurring during trailer playback.
    /// </summary>
    public bool EnableBackgroundBlur { get; set; } = false;

    /// <summary>
    /// Gets or sets a value indicating whether to enable debug logging.
    /// </summary>
    public bool EnableDebugLogging { get; set; } = true;


    /// <summary>
    /// Validates the current configuration and throws ConfigurationException if invalid.
    /// </summary>
    /// <exception cref="ConfigurationException">Thrown when configuration is invalid.</exception>
    public void Validate()
    {
        var errors = GetValidationErrors().ToList();
        if (errors.Any())
        {
            throw new ConfigurationException($"Configuration validation failed: {string.Join("; ", errors)}");
        }
    }

    /// <summary>
    /// Gets all validation errors for the current configuration.
    /// </summary>
    /// <returns>An enumerable of validation error messages.</returns>
    public IEnumerable<string> GetValidationErrors()
    {
        // Hover delay validation
        if (HoverDelayMs < 0)
        {
            yield return "Hover Delay cannot be negative";
        }
        else if (HoverDelayMs > 10000)
        {
            yield return "Hover Delay cannot exceed 10000ms (10 seconds)";
        }

        // Preview offset validations
        if (PreviewOffsetX < -2000 || PreviewOffsetX > 2000)
        {
            yield return "Preview Offset X must be between -2000 and 2000 pixels";
        }

        if (PreviewOffsetY < -2000 || PreviewOffsetY > 2000)
        {
            yield return "Preview Offset Y must be between -2000 and 2000 pixels";
        }

        // Preview size validations
        if (PreviewWidth < 200 || PreviewWidth > 800)
        {
            yield return "Preview Width must be between 200 and 800 pixels";
        }

        if (PreviewHeight < 150 || PreviewHeight > 600)
        {
            yield return "Preview Height must be between 150 and 600 pixels";
        }

        // Preview opacity validation
        if (PreviewOpacity < 0.0 || PreviewOpacity > 1.0)
        {
            yield return "Preview Opacity must be between 0.0 and 1.0";
        }

        // Preview border radius validation
        if (PreviewBorderRadius < 0 || PreviewBorderRadius > 50)
        {
            yield return "Preview Border Radius must be between 0 and 50 pixels";
        }

        // Preview size percentage validation
        if (PreviewSizePercentage < 50 || PreviewSizePercentage > 1500)
        {
            yield return "Preview Size Percentage must be between 50 and 1500";
        }

        // Preview volume validation
        if (PreviewVolume < 0 || PreviewVolume > 100)
        {
            yield return "Preview Volume must be between 0 and 100";
        }

        // Remote video quality validation
        var validQualities = new[] { "adaptive", "hd480", "hd720", "hd1080", "hd2160" };
        if (!validQualities.Any(q => string.Equals(q, RemoteVideoQuality, StringComparison.OrdinalIgnoreCase)))
        {
            yield return "Remote Video Quality must be one of: adaptive, hd480, hd720, hd1080, hd2160";
        }

        // Preview sizing mode validation
        if (!string.Equals(PreviewSizingMode, "Manual", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(PreviewSizingMode, "Percentage", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(PreviewSizingMode, "FitContent", StringComparison.OrdinalIgnoreCase))
        {
            yield return "Preview Sizing Mode must be 'Manual', 'Percentage', or 'FitContent'";
        }

        // Preview positioning mode validation
        if (!string.Equals(PreviewPositioningMode, "Center", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(PreviewPositioningMode, "Custom", StringComparison.OrdinalIgnoreCase))
        {
            yield return "Preview Positioning Mode must be 'Center' or 'Custom'";
        }
    }

    /// <summary>
    /// Checks if the current configuration is valid.
    /// </summary>
    /// <returns>True if configuration is valid, false otherwise.</returns>
    public bool IsValid()
    {
        return !GetValidationErrors().Any();
    }

    /// <summary>
    /// Validates a specific configuration property.
    /// </summary>
    /// <param name="propertyName">The name of the property to validate.</param>
    /// <returns>Validation error message if invalid, null if valid.</returns>
    public string? ValidateProperty(string propertyName)
    {
        return propertyName switch
        {
            nameof(HoverDelayMs) => ValidateHoverDelayMs(),
            nameof(PreviewOffsetX) => ValidatePreviewOffsetX(),
            nameof(PreviewOffsetY) => ValidatePreviewOffsetY(),
            nameof(PreviewWidth) => ValidatePreviewWidth(),
            nameof(PreviewHeight) => ValidatePreviewHeight(),
            nameof(PreviewOpacity) => ValidatePreviewOpacity(),
            nameof(PreviewBorderRadius) => ValidatePreviewBorderRadius(),
            nameof(PreviewSizePercentage) => ValidatePreviewSizePercentage(),
            nameof(PreviewVolume) => ValidatePreviewVolume(),
            nameof(RemoteVideoQuality) => ValidateRemoteVideoQuality(),
            nameof(PreviewSizingMode) => ValidatePreviewSizingMode(),
            nameof(PreviewPositioningMode) => ValidatePreviewPositioningMode(),
            _ => null
        };
    }

    private string? ValidateHoverDelayMs()
    {
        if (HoverDelayMs < 0)
            return "Hover Delay cannot be negative";
        if (HoverDelayMs > 10000)
            return "Hover Delay cannot exceed 10000ms (10 seconds)";
        return null;
    }

    private string? ValidatePreviewOffsetX()
    {
        if (PreviewOffsetX < -2000 || PreviewOffsetX > 2000)
            return "Preview Offset X must be between -2000 and 2000 pixels";
        return null;
    }

    private string? ValidatePreviewOffsetY()
    {
        if (PreviewOffsetY < -2000 || PreviewOffsetY > 2000)
            return "Preview Offset Y must be between -2000 and 2000 pixels";
        return null;
    }

    private string? ValidatePreviewWidth()
    {
        if (PreviewWidth < 200 || PreviewWidth > 800)
            return "Preview Width must be between 200 and 800 pixels";
        return null;
    }

    private string? ValidatePreviewHeight()
    {
        if (PreviewHeight < 150 || PreviewHeight > 600)
            return "Preview Height must be between 150 and 600 pixels";
        return null;
    }

    private string? ValidatePreviewOpacity()
    {
        if (PreviewOpacity < 0.0 || PreviewOpacity > 1.0)
            return "Preview Opacity must be between 0.0 and 1.0";
        return null;
    }

    private string? ValidatePreviewBorderRadius()
    {
        if (PreviewBorderRadius < 0 || PreviewBorderRadius > 50)
            return "Preview Border Radius must be between 0 and 50 pixels";
        return null;
    }

    private string? ValidatePreviewSizePercentage()
    {
        if (PreviewSizePercentage < 50 || PreviewSizePercentage > 1500)
            return "Preview Size Percentage must be between 50 and 1500";
        return null;
    }

    private string? ValidatePreviewVolume()
    {
        if (PreviewVolume < 0 || PreviewVolume > 100)
            return "Preview Volume must be between 0 and 100";
        return null;
    }

    private string? ValidateRemoteVideoQuality()
    {
        var validQualities = new[] { "adaptive", "hd480", "hd720", "hd1080", "hd2160" };
        if (!validQualities.Any(q => string.Equals(q, RemoteVideoQuality, StringComparison.OrdinalIgnoreCase)))
            return "Remote Video Quality must be one of: adaptive, hd480, hd720, hd1080, hd2160";
        return null;
    }

    private string? ValidatePreviewSizingMode()
    {
        if (!string.Equals(PreviewSizingMode, "Manual", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(PreviewSizingMode, "Percentage", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(PreviewSizingMode, "FitContent", StringComparison.OrdinalIgnoreCase))
            return "Preview Sizing Mode must be 'Manual', 'Percentage', or 'FitContent'";
        return null;
    }

    private string? ValidatePreviewPositioningMode()
    {
        if (!string.Equals(PreviewPositioningMode, "Center", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(PreviewPositioningMode, "Custom", StringComparison.OrdinalIgnoreCase))
            return "Preview Positioning Mode must be 'Center' or 'Custom'";
        return null;
    }
}