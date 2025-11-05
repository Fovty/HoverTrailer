namespace Fovty.Plugin.HoverTrailer.Models;

/// <summary>
/// Payload class for File Transformation plugin callbacks.
/// Must match the structure expected by File Transformation plugin.
/// </summary>
public class PatchRequestPayload
{
    /// <summary>
    /// Gets or sets the file contents to be transformed.
    /// </summary>
    public string? Contents { get; set; }
}