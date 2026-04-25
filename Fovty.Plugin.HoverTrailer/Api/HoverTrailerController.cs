using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Net.Mime;
using System.Threading.Tasks;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Fovty.Plugin.HoverTrailer.Exceptions;
using Fovty.Plugin.HoverTrailer.Helpers;
using Fovty.Plugin.HoverTrailer.Models;

namespace Fovty.Plugin.HoverTrailer.Api;

/// <summary>
/// The hover trailer controller.
/// </summary>
[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("HoverTrailer")]
[Produces(MediaTypeNames.Application.Json)]
public class HoverTrailerController : ControllerBase
{
    private readonly ILogger<HoverTrailerController> _logger;
    private readonly ILibraryManager _libraryManager;
    private readonly IServerConfigurationManager _serverConfigurationManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="HoverTrailerController"/> class.
    /// </summary>
    /// <param name="logger">Instance of the <see cref="ILogger{HoverTrailerController}"/> interface.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="serverConfigurationManager">Instance of the <see cref="IServerConfigurationManager"/> interface.</param>
    public HoverTrailerController(ILogger<HoverTrailerController> logger, ILibraryManager libraryManager, IServerConfigurationManager serverConfigurationManager)
    {
        _logger = logger;
        _libraryManager = libraryManager;
        _serverConfigurationManager = serverConfigurationManager;
    }

    /// <summary>
    /// Gets the client-side script for hover trailer functionality.
    /// </summary>
    /// <returns>The client script.</returns>
    [HttpGet("ClientScript")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    [Produces("application/javascript")]
    public ActionResult GetClientScript()
    {
        var requestId = GenerateRequestId();

        try
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null)
            {
                LoggingHelper.LogError(_logger, "Plugin configuration is null");
                var error = new ErrorResponse("CONFIG_ERROR", "Plugin configuration not available")
                {
                    RequestId = requestId
                };
                return StatusCode(500, error);
            }

            if (!config.EnableHoverPreview)
            {
                LoggingHelper.LogDebug(_logger, "Hover preview is disabled in configuration");
                var error = new ErrorResponse("FEATURE_DISABLED", "Hover preview is disabled")
                {
                    RequestId = requestId
                };
                return NotFound(error);
            }

            // Validate configuration before serving script
            var validationErrors = config.GetValidationErrors().ToList();
            if (validationErrors.Any())
            {
                LoggingHelper.LogWarning(_logger, "Configuration validation failed: {Errors}", string.Join("; ", validationErrors));
                var error = ErrorResponse.FromConfigurationErrors(validationErrors, requestId);
                return BadRequest(error);
            }

            var basePath = GetBasePath();
            var script = GetHoverTrailerScript(config, basePath);
            LoggingHelper.LogDebug(_logger, "Successfully served client script with base path: {BasePath}", basePath);
            return Content(script, "application/javascript");
        }
        catch (ConfigurationException ex)
        {
            LoggingHelper.LogError(_logger, ex, "Configuration error serving client script");
            var error = ErrorResponse.FromException(ex, requestId);
            return BadRequest(error);
        }
        catch (Exception ex)
        {
            LoggingHelper.LogError(_logger, ex, "Unexpected error serving client script");
            var error = ErrorResponse.FromException(ex, requestId);
            return StatusCode(500, error);
        }
    }

    /// <summary>
    /// Gets trailer information for a specific item (Movie or Series).
    /// </summary>
    /// <param name="itemId">The item ID.</param>
    /// <returns>The trailer information.</returns>
    [HttpGet("TrailerInfo/{itemId}")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public ActionResult<TrailerInfo> GetTrailerInfo([FromRoute] Guid itemId)
    {
        var requestId = GenerateRequestId();

        try
        {
            if (itemId == Guid.Empty)
            {
                LoggingHelper.LogWarning(_logger, "Invalid item ID provided: {ItemId}", itemId);
                var invalidError = new ErrorResponse("INVALID_ARGUMENT", "Item ID cannot be empty")
                {
                    RequestId = requestId
                };
                return BadRequest(invalidError);
            }

            var item = _libraryManager.GetItemById(itemId);
            if (item == null)
            {
                LoggingHelper.LogDebug(_logger, "Item not found with ID: {ItemId}", itemId);
                var notFoundError = new ErrorResponse("ITEM_NOT_FOUND", "Item not found", $"No item found with ID: {itemId}")
                {
                    RequestId = requestId
                };
                return NotFound(notFoundError);
            }

            // Multi-source trailer detection with priority: Local → Remote → Downloaded
            LoggingHelper.LogDebug(_logger, "Starting multi-source trailer detection for item: {ItemName} (ID: {ItemId})", item.Name, itemId);
            LoggingHelper.LogDebug(_logger, "Item path: {ItemPath}", item.Path ?? "null");
            LoggingHelper.LogDebug(_logger, "Item directory: {ItemDirectory}", item.Path != null ? System.IO.Path.GetDirectoryName(item.Path) ?? "null" : "null");

            TrailerInfo? trailerInfo = null;

            // Step 1: Check for local trailers using the same approach as Jellyfin's native implementation
            LoggingHelper.LogDebug(_logger, "Step 1: Checking for local trailers...");

            IEnumerable<BaseItem> localTrailers;
            if (item is IHasTrailers hasTrailers)
            {
                // Use LocalTrailers property which matches Jellyfin's native trailer selection
                localTrailers = hasTrailers.LocalTrailers;
                LoggingHelper.LogDebug(_logger, "Using LocalTrailers property: Found {LocalTrailerCount} local trailers for item: {ItemName}",
                    localTrailers.Count(), item.Name);
            }
            else
            {
                // Fallback to GetExtras if item doesn't implement IHasTrailers
                localTrailers = item.GetExtras(new[] { ExtraType.Trailer });
                LoggingHelper.LogDebug(_logger, "Using GetExtras fallback: Found {LocalTrailerCount} local trailers for item: {ItemName}",
                    localTrailers.Count(), item.Name);
            }

            // Log detailed information about each local trailer found
            var localTrailerList = localTrailers.ToList();
            for (int i = 0; i < localTrailerList.Count; i++)
            {
                var t = localTrailerList[i];
                LoggingHelper.LogDebug(_logger, "Local Trailer {Index}: ID={TrailerId}, Name={TrailerName}, Path={TrailerPath}",
                    i + 1, t.Id, t.Name, t.Path);
            }

            var localTrailer = localTrailerList.FirstOrDefault();

            if (localTrailer != null)
            {
                LoggingHelper.LogDebug(_logger, "Found local trailer for item: {ItemName} (ID: {ItemId})", item.Name, itemId);
                LoggingHelper.LogDebug(_logger, "Local trailer details - ID: {TrailerId}, Name: {TrailerName}, Path: {TrailerPath}",
                    localTrailer.Id, localTrailer.Name, localTrailer.Path);

                trailerInfo = new TrailerInfo
                {
                    Id = localTrailer.Id,
                    Name = localTrailer.Name,
                    Path = localTrailer.Path,
                    RunTimeTicks = localTrailer.RunTimeTicks,
                    HasSubtitles = false, // Default value since BaseItem doesn't have HasSubtitles
                    TrailerType = TrailerType.Local,
                    IsRemote = false,
                    Source = "Local File"
                };

                LoggingHelper.LogDebug(_logger, "Successfully created local trailer info for item: {ItemName} (ID: {ItemId})",
                    item.Name, itemId);
                return Ok(trailerInfo);
            }

            // Step 2: Check for remote trailers if no local trailer found
            LoggingHelper.LogDebug(_logger, "Step 2: No local trailer found, checking for remote trailers...");

            if (item.RemoteTrailers?.Any() == true)
            {
                var remoteTrailer = item.RemoteTrailers.LastOrDefault();
                LoggingHelper.LogDebug(_logger, "Found remote trailer for item: {ItemName} (ID: {ItemId})", item.Name, itemId);

                trailerInfo = new TrailerInfo
                {
                    Id = itemId, // Use item ID since remote trailers don't have their own ID
                    Name = remoteTrailer.Name ?? $"{item.Name} - Trailer",
                    Path = remoteTrailer.Url,
                    RunTimeTicks = null, // Remote trailers typically don't have runtime info
                    HasSubtitles = false, // Remote trailers typically don't have subtitle info
                    TrailerType = TrailerType.Remote,
                    IsRemote = true,
                    Source = GetTrailerSource(remoteTrailer.Url)
                };

                LoggingHelper.LogDebug(_logger, "Successfully created remote trailer info for item: {ItemName} (ID: {ItemId}), Source: {Source}",
                    item.Name, itemId, trailerInfo.Source);
                return Ok(trailerInfo);
            }

            // Step 3: Check for theme videos as fallback
            var pluginConfig = Plugin.Instance?.Configuration;
            if (pluginConfig?.EnableThemeVideoFallback == true)
            {
                LoggingHelper.LogDebug(_logger, "Step 3: No trailer found, checking for theme video fallback...");
                var themeVideos = item.GetThemeVideos();
                var themeVideo = themeVideos.FirstOrDefault();
                if (themeVideo != null)
                {
                    LoggingHelper.LogDebug(_logger, "Found theme video for item: {ItemName} (ID: {ItemId})", item.Name, itemId);
                    trailerInfo = new TrailerInfo
                    {
                        Id = themeVideo.Id,
                        Name = themeVideo.Name ?? $"{item.Name} - Theme",
                        Path = themeVideo.Path,
                        RunTimeTicks = themeVideo.RunTimeTicks,
                        HasSubtitles = false,
                        TrailerType = TrailerType.ThemeVideo,
                        IsRemote = false,
                        Source = "Theme Video"
                    };
                    return Ok(trailerInfo);
                }
            }

            // Step 4: No trailers found (local, remote, or theme video)
            LoggingHelper.LogDebug(_logger, "No local or remote trailers found for item: {ItemName} (ID: {ItemId})", item.Name, itemId);

            // Also check if there are any files in the item directory that might be trailers (for debugging)
            var itemDir = System.IO.Path.GetDirectoryName(item.Path);
            if (!string.IsNullOrEmpty(itemDir) && System.IO.Directory.Exists(itemDir))
            {
                var files = System.IO.Directory.GetFiles(itemDir, "*", System.IO.SearchOption.TopDirectoryOnly);
                LoggingHelper.LogDebug(_logger, "Files in item directory {ItemDir}: {Files}",
                    itemDir, string.Join(", ", files.Select(System.IO.Path.GetFileName)));

                // Look for potential trailer files
                var potentialTrailers = files.Where(f =>
                    f.Contains("trailer", StringComparison.OrdinalIgnoreCase) ||
                    f.Contains("-trailer", StringComparison.OrdinalIgnoreCase) ||
                    f.Contains(".trailer.", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (potentialTrailers.Any())
                {
                    LoggingHelper.LogDebug(_logger, "Potential trailer files found but not detected by Jellyfin: {PotentialTrailers}",
                        string.Join(", ", potentialTrailers.Select(System.IO.Path.GetFileName)));
                }
                else
                {
                    LoggingHelper.LogDebug(_logger, "No potential trailer files found in directory");
                }
            }

            var error = new ErrorResponse("TRAILER_NOT_FOUND", "No trailer found for this item",
                $"Item '{item.Name}' does not have any local or remote trailers available")
            {
                RequestId = requestId
            };
            return NotFound(error);
        }
        catch (UnauthorizedAccessException ex)
        {
            LoggingHelper.LogError(_logger, ex, "Unauthorized access getting trailer info for item {ItemId}", itemId);
            var error = ErrorResponse.FromException(ex, requestId);
            return StatusCode(403, error);
        }
        catch (Exception ex)
        {
            LoggingHelper.LogError(_logger, ex, "Unexpected error getting trailer info for item {ItemId}", itemId);
            var error = ErrorResponse.FromException(ex, requestId);
            return StatusCode(500, error);
        }
    }

    /// <summary>
    /// Gets all movies that have trailers available for hover preview.
    /// </summary>
    /// <returns>List of movies with trailers.</returns>
    [HttpGet("MoviesWithTrailers")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public ActionResult<IEnumerable<MovieTrailerInfo>> GetMoviesWithTrailers()
    {
        var requestId = GenerateRequestId();

        try
        {
            LoggingHelper.LogDebug(_logger, "Retrieving all movies with trailers");

            var movies = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { Jellyfin.Data.Enums.BaseItemKind.Movie },
                IsVirtualItem = false,
                Recursive = true
            }).OfType<Movie>().ToList();

            var moviesWithTrailers = movies
                .Select(m =>
                {
                    var movieTrailers = m.GetExtras(new[] { ExtraType.Trailer });
                    return new { Movie = m, Trailers = movieTrailers };
                })
                .Where(x => x.Trailers.Any())
                .Select(x => new MovieTrailerInfo
                {
                    Id = x.Movie.Id,
                    Name = x.Movie.Name,
                    HasTrailer = true,
                    TrailerCount = x.Trailers.Count()
                })
                .ToList();

            LoggingHelper.LogDebug(_logger, "Successfully retrieved {Count} movies with trailers", moviesWithTrailers.Count);
            return Ok(moviesWithTrailers);
        }
        catch (UnauthorizedAccessException ex)
        {
            LoggingHelper.LogError(_logger, ex, "Unauthorized access getting movies with trailers");
            var error = ErrorResponse.FromException(ex, requestId);
            return StatusCode(403, error);
        }
        catch (Exception ex)
        {
            LoggingHelper.LogError(_logger, ex, "Unexpected error getting movies with trailers");
            var error = ErrorResponse.FromException(ex, requestId);
            return StatusCode(500, error);
        }
    }

    /// <summary>
    /// Gets the configuration status for the plugin.
    /// </summary>
    /// <returns>The configuration status.</returns>
    [HttpGet("Status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public ActionResult<HoverTrailerStatus> GetStatus()
    {
        var requestId = GenerateRequestId();

        try
        {
            LoggingHelper.LogDebug(_logger, "Retrieving plugin status");

            var config = Plugin.Instance?.Configuration;
            var status = new HoverTrailerStatus
            {
                IsEnabled = config?.EnableHoverPreview ?? false,
                HoverDelayMs = config?.HoverDelayMs ?? 1000
            };

            LoggingHelper.LogDebug(_logger, "Successfully retrieved plugin status");
            return Ok(status);
        }
        catch (Exception ex)
        {
            LoggingHelper.LogError(_logger, ex, "Unexpected error getting plugin status");
            var error = ErrorResponse.FromException(ex, requestId);
            return StatusCode(500, error);
        }
    }

    /// <summary>
    /// Retrieves the base path from Jellyfin's network configuration.
    /// </summary>
    /// <returns>The configured base path or empty string if unavailable.</returns>
    private string GetBasePath()
    {
        try
        {
            LoggingHelper.LogDebug(_logger, "Retrieving base path from network configuration...");

            var networkConfig = _serverConfigurationManager.GetConfiguration("network");
            var configType = networkConfig.GetType();
            var basePathField = configType.GetProperty("BaseUrl");
            var confBasePath = basePathField?.GetValue(networkConfig)?.ToString()?.Trim('/');

            var basePath = string.IsNullOrEmpty(confBasePath) ? "" : "/" + confBasePath;

            LoggingHelper.LogDebug(_logger, "Retrieved base path: '{BasePath}'", basePath);
            return basePath;
        }
        catch (Exception ex)
        {
            LoggingHelper.LogWarning(_logger, "Unable to get base path from network configuration, using default '': {Message}", ex.Message);
            LoggingHelper.LogDebug(_logger, "Base path retrieval error details: {Exception}", ex.ToString());
            return "";
        }
    }

    /// <summary>
    /// Gets the hover trailer client script.
    /// </summary>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="basePath">The base path for API URLs.</param>
    /// <returns>The client script.</returns>
    private static string GetHoverTrailerScript(Configuration.PluginConfiguration config, string basePath)
    {
        return $@"
(function() {{
    'use strict';

    const BASE_PATH = '{basePath}';
    const API_BASE_URL = window.location.origin + BASE_PATH;
    const HOVER_DELAY = {config.HoverDelayMs};
    const DEBUG_LOGGING = {config.EnableDebugLogging.ToString().ToLower()};
    const PREVIEW_POSITIONING_MODE = '{config.PreviewPositioningMode}';
    const PREVIEW_OFFSET_X = {config.PreviewOffsetX};
    const PREVIEW_OFFSET_Y = {config.PreviewOffsetY};
    const PREVIEW_WIDTH = {config.PreviewWidth};
    const PREVIEW_HEIGHT = {config.PreviewHeight};
    const PREVIEW_OPACITY = {config.PreviewOpacity.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)};
    const PREVIEW_BORDER_RADIUS = {config.PreviewBorderRadius};
    const PREVIEW_SIZING_MODE = '{config.PreviewSizingMode}';
    const PREVIEW_SIZE_PERCENTAGE = {config.PreviewSizePercentage};
    const ENABLE_PREVIEW_AUDIO = {config.EnablePreviewAudio.ToString().ToLower()};
    const PREVIEW_VOLUME = {config.PreviewVolume};
    const REMOTE_VIDEO_QUALITY = '{config.RemoteVideoQuality}';
    // Native iframe render dimensions tied to the requested quality. YouTube's
    // adaptive selector reads the iframe's pixel dimensions: ≥1280px wide →
    // 720p, ≥1920px → 1080p. We render at the target tier and let CSS
    // transform: scale(...) fit the container, so even tiny poster cards get
    // an HD source stream instead of YouTube serving 240/360p to undersized
    // iframes (the prior behaviour). Container CSS dimensions still match
    // FitContent / Manual sizing exactly — only the iframe element's intrinsic
    // pixel buffer changes.
    const NATIVE_IFRAME_DIMS = (() => {{
        switch (REMOTE_VIDEO_QUALITY) {{
            case 'hd2160': return {{ w: 3840, h: 2160 }};
            case 'hd1080': return {{ w: 1920, h: 1080 }};
            case 'hd720':  return {{ w: 1280, h: 720 }};
            case 'hd480':  return {{ w: 854,  h: 480 }};
            default:       return {{ w: 1920, h: 1080 }}; // 'adaptive'
        }}
    }})();
    const BACKGROUND_BLUR_MODE = '{config.GetEffectiveBackgroundBlurMode()}';
    const BACKGROUND_BLUR_FALLOFF_RADIUS = {config.BackgroundBlurFalloffRadius};
    const ENABLE_TOAST_NOTIFICATIONS = {config.EnableToastNotifications.ToString().ToLower()};
    const ENABLE_HOVER_PROGRESS_INDICATOR = {config.EnableHoverProgressIndicator.ToString().ToLower()};
    const ENABLE_PERSISTENT_PREVIEW = {config.EnablePersistentPreview.ToString().ToLower()};
    const ENABLE_FOCUS_TRIGGER = {config.EnableFocusTrigger.ToString().ToLower()};
    const ENABLE_ANCHOR_PIN_TO_VIEWPORT = {config.EnableAnchorPinToViewport.ToString().ToLower()};
    // Effective gating: trailer controls require Persistent Preview to be
    // useful (otherwise the preview vanishes on mouseleave before the user
    // can reach the controls). Compute the effective bool server-side so the
    // JS doesn't have to re-check.
    const ENABLE_TRAILER_CONTROLS = {(config.EnablePersistentPreview && config.EnableTrailerControls).ToString().ToLower()};

    // Disable on touch devices: hover UX doesn't apply and mobile WebViews
    // exhibit freezes around iframe cleanup (issue #15).
    const isTouchDevice =
        /android|iphone|ipad|ipod/i.test(navigator.userAgent) ||
        navigator.maxTouchPoints > 1 ||
        window.matchMedia('(hover: none)').matches;
    if (isTouchDevice) {{
        console.log('[HoverTrailer] Touch device detected, plugin disabled');
        return;
    }}

    let hoverTimeout;
    let currentPreview;
    let currentCardElement;
    let isPlaying = false;
    let resizeHandler;
    let anchorTrackerRafId = null;
    let anchorScrollHandler = null; // capture-phase scroll listener for AnchorToCard
    let blurHaloScrollHandler = null; // capture-phase scroll listener — translates backdrop to follow preview
    let blurHaloAnchorLeft = 0;      // viewport coords of preview when mask was rendered
    let blurHaloAnchorTop = 0;
    let blurHaloAnchorWidth = 0;     // preview size when mask was rendered — re-render on change
    let blurHaloAnchorHeight = 0;
    let persistentDismissHandlers = null; // {{click, keydown}} when ENABLE_PERSISTENT_PREVIEW is active
    // After exiting fullscreen, the same Escape press would otherwise cascade
    // into hidePreview(). The trailer controls' fullscreenchange listener
    // sets this flag for ~250 ms to swallow that follow-up Escape.
    let suppressEscapeOnce = false;
    // Browsers fire a 'resize' event AFTER fullscreenchange has already
    // cleared document.fullscreenElement when exiting FS. Without a guard,
    // resizeHandler sees no fullscreen and tears down the preview. Set
    // this in fullscreenchange and check in resizeHandler.
    let recentFsExit = false;

    // Inflate the halo backdrop beyond the viewport so we can translate it
    // freely during scroll without exposing unblurred edges.
    const HALO_INFLATE = 1500;
    // Keep AnchorToCard previews fully on-screen when a card sits near the
    // viewport edge. Margin is a small cosmetic gap. If the preview is wider
    // or taller than the viewport (user-configured percentage too aggressive),
    // pin to the margin — never push past.
    const ANCHOR_VIEWPORT_MARGIN = 8;
    function clampAnchorToViewport(anchorX, anchorY, width, height) {{
        const vw = window.innerWidth;
        const vh = window.innerHeight;
        const maxX = Math.max(ANCHOR_VIEWPORT_MARGIN, vw - width - ANCHOR_VIEWPORT_MARGIN);
        const maxY = Math.max(ANCHOR_VIEWPORT_MARGIN, vh - height - ANCHOR_VIEWPORT_MARGIN);
        return {{
            x: Math.min(maxX, Math.max(ANCHOR_VIEWPORT_MARGIN, anchorX)),
            y: Math.min(maxY, Math.max(ANCHOR_VIEWPORT_MARGIN, anchorY)),
        }};
    }}
    let attachedCards = new WeakSet(); // Track actual card elements that already have listeners
    let mutationDebounce = null;
    let currentToast = null;
    let toastTimeout = null;
    let previewGeneration = 0;
    let currentAbortController = null;

    function log(message, ...args) {{
        if (DEBUG_LOGGING) {{
            console.log('[HoverTrailer]', message, ...args);
        }}
    }}

    // Toast notification styles
    const toastStyles = document.createElement('style');
    toastStyles.textContent = `
        .hovertrailer-toast {{
            position: fixed;
            bottom: 24px;
            right: 24px;
            padding: 12px 20px;
            border-radius: 8px;
            font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Oxygen, Ubuntu, sans-serif;
            font-size: 14px;
            font-weight: 500;
            color: #fff;
            z-index: 10001;
            opacity: 0;
            transform: translateY(10px);
            transition: opacity 0.3s ease, transform 0.3s ease;
            pointer-events: none;
            display: flex;
            align-items: center;
            gap: 10px;
            box-shadow: 0 4px 12px rgba(0, 0, 0, 0.4);
            backdrop-filter: blur(10px);
            -webkit-backdrop-filter: blur(10px);
        }}
        .hovertrailer-toast.visible {{
            opacity: 1;
            transform: translateY(0);
        }}
        .hovertrailer-toast.loading {{
            background: rgba(0, 120, 212, 0.9);
        }}
        .hovertrailer-toast.error {{
            background: rgba(200, 120, 40, 0.9);
        }}
        .hovertrailer-toast.success {{
            background: rgba(40, 167, 69, 0.9);
        }}
        .hovertrailer-toast-spinner {{
            width: 16px;
            height: 16px;
            border: 2px solid rgba(255, 255, 255, 0.3);
            border-top-color: #fff;
            border-radius: 50%;
            animation: hovertrailer-spin 0.8s linear infinite;
        }}
        @keyframes hovertrailer-spin {{
            to {{ transform: rotate(360deg); }}
        }}
    `;
    document.head.appendChild(toastStyles);

    // Trailer controls styles (play/pause, seek, volume, fullscreen overlay)
    const trailerControlsStyles = document.createElement('style');
    trailerControlsStyles.textContent = `
        .ht-controls {{
            position: absolute;
            bottom: 0;
            left: 0;
            right: 0;
            display: flex;
            align-items: center;
            gap: 8px;
            padding: 36px 12px 10px;
            color: #fff;
            z-index: 2;
            opacity: 0;
            transition: opacity 0.2s ease;
            pointer-events: auto;
            font: 12px -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif;
            user-select: none;
            box-sizing: border-box;
        }}
        .ht-controls-bg {{
            position: absolute;
            inset: 0;
            background: linear-gradient(transparent, rgba(0, 0, 0, 0.78));
            pointer-events: none;
            z-index: -1;
        }}
        .ht-controls.visible {{ opacity: 1; }}
        .ht-control-btn {{
            flex: 0 0 auto;
            width: 28px;
            height: 28px;
            padding: 0;
            border: none;
            background: transparent;
            color: #fff;
            cursor: pointer;
            border-radius: 4px;
            display: flex;
            align-items: center;
            justify-content: center;
            transition: background 0.15s ease;
        }}
        .ht-control-btn:hover {{ background: rgba(255, 255, 255, 0.18); }}
        .ht-control-btn:active {{ background: rgba(255, 255, 255, 0.28); }}
        .ht-control-btn svg {{ width: 20px; height: 20px; display: block; }}
        .ht-time {{
            font-variant-numeric: tabular-nums;
            flex: 0 0 auto;
            min-width: 32px;
            text-align: center;
            text-shadow: 0 1px 2px rgba(0, 0, 0, 0.6);
        }}
        /* Seek + volume sliders use a 16px tall clickable area with a 4px
           visible track centred inside — so the visual line stays thin but
           the user has a comfortable hit zone to drag. */
        .ht-seek {{
            flex: 1 1 auto;
            -webkit-appearance: none;
            appearance: none;
            height: 16px;
            background: transparent;
            cursor: pointer;
            margin: 0;
            padding: 0;
            min-width: 40px;
            outline: none;
        }}
        .ht-seek::-webkit-slider-runnable-track {{
            height: 4px;
            background: rgba(255, 255, 255, 0.3);
            border-radius: 2px;
        }}
        .ht-seek::-moz-range-track {{
            height: 4px;
            background: rgba(255, 255, 255, 0.3);
            border-radius: 2px;
            border: none;
        }}
        .ht-seek::-webkit-slider-thumb {{
            -webkit-appearance: none;
            width: 12px;
            height: 12px;
            margin-top: -4px;
            border-radius: 50%;
            background: #00a4dc;
            border: none;
            cursor: pointer;
            box-shadow: 0 1px 3px rgba(0, 0, 0, 0.5);
        }}
        .ht-seek::-moz-range-thumb {{
            width: 12px;
            height: 12px;
            border-radius: 50%;
            background: #00a4dc;
            border: none;
            cursor: pointer;
            box-shadow: 0 1px 3px rgba(0, 0, 0, 0.5);
        }}
        .ht-volume-wrap {{
            display: flex;
            align-items: center;
            gap: 4px;
            flex: 0 0 auto;
        }}
        .ht-volume {{
            -webkit-appearance: none;
            appearance: none;
            width: 60px;
            height: 14px;
            background: transparent;
            cursor: pointer;
            margin: 0;
            padding: 0;
            outline: none;
        }}
        .ht-volume::-webkit-slider-runnable-track {{
            height: 4px;
            background: rgba(255, 255, 255, 0.3);
            border-radius: 2px;
        }}
        .ht-volume::-moz-range-track {{
            height: 4px;
            background: rgba(255, 255, 255, 0.3);
            border-radius: 2px;
            border: none;
        }}
        .ht-volume::-webkit-slider-thumb {{
            -webkit-appearance: none;
            width: 10px;
            height: 10px;
            margin-top: -3px;
            border-radius: 50%;
            background: #fff;
            border: none;
            cursor: pointer;
        }}
        .ht-volume::-moz-range-thumb {{
            width: 10px;
            height: 10px;
            border-radius: 50%;
            background: #fff;
            border: none;
            cursor: pointer;
        }}
        /* Invisible mask over the iframe — absorbs clicks/taps so YouTube's
           player doesn't show its native tap-feedback overlay. Mouse events
           still drive control visibility. Sits above the iframe but below
           the controls bar. */
        .ht-iframe-mask {{
            position: absolute;
            inset: 0;
            z-index: 1;
            background: transparent;
            pointer-events: auto;
            cursor: default;
        }}
        /* In fullscreen the iframe must fill the FS surface (override the
           inline transform/size set for the small preview). */
        :fullscreen iframe[id^=""youtube-preview-""] {{
            width: 100vw !important;
            height: 100vh !important;
            transform: none !important;
            top: 0 !important;
            left: 0 !important;
        }}
        :fullscreen .ht-controls {{ opacity: 0; }}
        :fullscreen:hover .ht-controls,
        :fullscreen .ht-controls:hover {{ opacity: 1; }}
    `;
    document.head.appendChild(trailerControlsStyles);

    // Progress indicator styles
    const progressStyles = document.createElement('style');
    progressStyles.textContent = `
        .hovertrailer-progress {{
            position: absolute;
            bottom: 0;
            left: 0;
            height: 3px;
            background: linear-gradient(90deg, #00a4dc, #00d4ff);
            border-radius: 0 2px 0 0;
            z-index: 1000;
            width: 0%;
            animation: hovertrailer-progress-fill linear forwards;
            box-shadow: 0 0 6px rgba(0, 164, 220, 0.6);
        }}
        @keyframes hovertrailer-progress-fill {{
            from {{ width: 0%; }}
            to {{ width: 100%; }}
        }}
    `;
    document.head.appendChild(progressStyles);

    function showToast(message, type = 'loading', duration = 0) {{
        if (!ENABLE_TOAST_NOTIFICATIONS) return;

        hideToast();

        const toast = document.createElement('div');
        toast.className = `hovertrailer-toast ${{type}}`;

        if (type === 'loading') {{
            const spinner = document.createElement('div');
            spinner.className = 'hovertrailer-toast-spinner';
            toast.appendChild(spinner);
        }}

        const text = document.createElement('span');
        text.textContent = message;
        toast.appendChild(text);

        document.body.appendChild(toast);
        currentToast = toast;

        // Trigger reflow for animation
        toast.offsetHeight;
        toast.classList.add('visible');

        log('Toast shown:', message, type);

        if (duration > 0) {{
            toastTimeout = setTimeout(() => {{
                hideToast();
            }}, duration);
        }}
    }}

    function hideToast() {{
        if (toastTimeout) {{
            clearTimeout(toastTimeout);
            toastTimeout = null;
        }}
        if (currentToast) {{
            const toastToRemove = currentToast;
            currentToast = null;
            toastToRemove.classList.remove('visible');
            setTimeout(() => {{
                if (toastToRemove.parentNode) {{
                    toastToRemove.parentNode.removeChild(toastToRemove);
                }}
            }}, 300);
            log('Toast hidden');
        }}
    }}

    function applyBackgroundBlur() {{
        if (BACKGROUND_BLUR_MODE === 'Off') return;

        let backdrop = document.getElementById('hover-trailer-backdrop');
        const isHalo = BACKGROUND_BLUR_MODE === 'Halo';
        const inflate = isHalo ? HALO_INFLATE : 0;
        if (!backdrop) {{
            backdrop = document.createElement('div');
            backdrop.id = 'hover-trailer-backdrop';
            // Halo: oversize the element so we can translate it on scroll
            // without exposing unblurred screen edges. Full mode stays exactly
            // viewport-sized.
            backdrop.style.cssText = `
                position: fixed;
                top: ${{-inflate}}px;
                left: ${{-inflate}}px;
                width: calc(100vw + ${{inflate * 2}}px);
                height: calc(100vh + ${{inflate * 2}}px);
                backdrop-filter: blur(20px);
                -webkit-backdrop-filter: blur(20px);
                background: rgba(0, 0, 0, 0.05);
                z-index: 9999;
                pointer-events: none;
                opacity: 0;
                transition: opacity 0.3s ease;
                will-change: transform;
            `;
            document.body.appendChild(backdrop);
        }}

        if (isHalo) {{
            // Render the mask ONCE with the cutout at the preview's current
            // viewport position, then keep it aligned with the preview by
            // translating the whole backdrop on scroll. The mask never
            // regenerates, so the GPU only does cheap composited translates
            // and the backdrop-filter never has to re-rasterize a new mask.
            renderHaloMask(backdrop);
            if (blurHaloScrollHandler !== null) {{
                window.removeEventListener('scroll', blurHaloScrollHandler, {{ capture: true }});
            }}
            blurHaloScrollHandler = () => {{ trackHaloPosition(backdrop); }};
            window.addEventListener('scroll', blurHaloScrollHandler, {{ passive: true, capture: true }});
        }} else {{
            backdrop.style.maskImage = '';
            backdrop.style.webkitMaskImage = '';
            backdrop.style.transform = '';
        }}

        setTimeout(() => {{ backdrop.style.opacity = '1'; }}, 10);
        log('Background blur applied (' + BACKGROUND_BLUR_MODE + ')');
    }}

    // Renders the halo SVG mask. The mask is a single blurred white rect
    // centred on the preview — no inner cutout. The preview container itself
    // (z-index 10000, opaque background) sits in front of the backdrop and
    // covers the blur naturally where the video is, so the halo can never
    // visually misalign with the trailer edge. Coordinates are in the
    // inflated backdrop's local space ((HALO_INFLATE, HALO_INFLATE) → viewport
    // origin); trackHaloPosition translates the backdrop on scroll.
    function renderHaloMask(backdrop) {{
        if (!currentPreview) return;
        const rect = currentPreview.getBoundingClientRect();
        blurHaloAnchorLeft = rect.left;
        blurHaloAnchorTop = rect.top;
        blurHaloAnchorWidth = rect.width;
        blurHaloAnchorHeight = rect.height;
        const W = window.innerWidth;
        const H = window.innerHeight;
        const CW = W + HALO_INFLATE * 2;
        const CH = H + HALO_INFLATE * 2;
        const r = BACKGROUND_BLUR_FALLOFF_RADIUS;
        const blurStd = Math.max(1, Math.round(r / 3));
        const vL = Math.round(rect.left + HALO_INFLATE);
        const vT = Math.round(rect.top + HALO_INFLATE);
        const vW = Math.round(rect.width);
        const vH = Math.round(rect.height);
        const br = PREVIEW_BORDER_RADIUS;
        const svg = `<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 ${{CW}} ${{CH}}' preserveAspectRatio='none'><defs><filter id='b' x='-50%' y='-50%' width='200%' height='200%'><feGaussianBlur stdDeviation='${{blurStd}}'/></filter></defs><rect width='100%' height='100%' fill='black'/><rect x='${{vL - r}}' y='${{vT - r}}' width='${{vW + 2 * r}}' height='${{vH + 2 * r}}' rx='${{br + r}}' ry='${{br + r}}' fill='white' filter='url(#b)'/></svg>`;
        const url = `url(""data:image/svg+xml;utf8,${{encodeURIComponent(svg)}}"")`;
        backdrop.style.maskImage = url;
        backdrop.style.webkitMaskImage = url;
        backdrop.style.maskMode = 'luminance';
        backdrop.style.webkitMaskMode = 'luminance';
        backdrop.style.transform = 'translate3d(0, 0, 0)';
    }}

    // Cheap per-scroll update: read preview's current viewport position, set
    // a translate3d on the backdrop equal to the delta from where the mask
    // was anchored. If the delta exceeds the inflate buffer, re-render the
    // mask at the new position and reset the delta to zero.
    function trackHaloPosition(backdrop) {{
        if (!currentPreview) return;
        const rect = currentPreview.getBoundingClientRect();
        const dx = rect.left - blurHaloAnchorLeft;
        const dy = rect.top - blurHaloAnchorTop;
        // Size change (FitContent loadedmetadata, settings change) invalidates
        // the cutout dimensions — must re-render, can't translate.
        const sizeChanged = Math.abs(rect.width - blurHaloAnchorWidth) > 1
                         || Math.abs(rect.height - blurHaloAnchorHeight) > 1;
        if (sizeChanged || Math.abs(dx) > HALO_INFLATE * 0.6 || Math.abs(dy) > HALO_INFLATE * 0.6) {{
            renderHaloMask(backdrop);
        }} else {{
            backdrop.style.transform = `translate3d(${{Math.round(dx)}}px, ${{Math.round(dy)}}px, 0)`;
        }}
    }}

    function removeBackgroundBlur() {{
        if (blurHaloScrollHandler !== null) {{
            window.removeEventListener('scroll', blurHaloScrollHandler, {{ capture: true }});
            blurHaloScrollHandler = null;
        }}
        const backdrop = document.getElementById('hover-trailer-backdrop');
        if (backdrop) {{
            backdrop.style.opacity = '0';
            setTimeout(() => {{
                if (backdrop.parentNode) {{
                    backdrop.parentNode.removeChild(backdrop);
                }}
            }}, 300);
            log('Background blur removed');
        }}
    }}

    function showProgressIndicator(card) {{
        if (!ENABLE_HOVER_PROGRESS_INDICATOR) return;
        const cardPosition = window.getComputedStyle(card).position;
        if (cardPosition === 'static') {{
            card.style.position = 'relative';
            card.dataset.hovertrailerPositionSet = 'true';
        }}
        const bar = document.createElement('div');
        bar.className = 'hovertrailer-progress';
        bar.style.animationDuration = `${{HOVER_DELAY}}ms`;
        card.appendChild(bar);
        log('Progress indicator shown on card');
    }}

    function hideProgressIndicator(card) {{
        if (!card) return;
        const bar = card.querySelector('.hovertrailer-progress');
        if (bar) {{
            bar.parentNode.removeChild(bar);
            log('Progress indicator hidden');
        }}
        if (card.dataset.hovertrailerPositionSet) {{
            card.style.position = '';
            delete card.dataset.hovertrailerPositionSet;
        }}
    }}

    function extractYouTubeVideoId(url) {{
        // Extract video ID from various YouTube URL formats
        const patterns = [
            /(?:youtube\.com\/watch\?v=|youtu\.be\/)([^&\?]+)/,
            /youtube\.com\/embed\/([^&\?]+)/,
            /youtube\.com\/v\/([^&\?]+)/
        ];

        for (const pattern of patterns) {{
            const match = url.match(pattern);
            if (match && match[1]) {{
                log('Extracted YouTube video ID:', match[1]);
                return match[1];
            }}
        }}

        log('Failed to extract video ID from URL:', url);
        return null;
    }}

    function createYouTubePreview(embedUrl, cardElement) {{
        // Create container div for the YouTube iframe
        const container = document.createElement('div');
        const iframe = document.createElement('iframe');

        // Get card position relative to viewport (needed for custom positioning)
        const cardRect = cardElement.getBoundingClientRect();
        const cardCenterX = cardRect.left + cardRect.width / 2;
        const cardCenterY = cardRect.top + cardRect.height / 2;

        // Calculate container size based on sizing mode
        let containerWidth, containerHeight;
        if (PREVIEW_SIZING_MODE === 'FitContent') {{
            // For fit content mode with YouTube (16:9 aspect ratio)
            // Apply the same logic as local videos
            const youtubeAspectRatio = 16 / 9;  // YouTube standard aspect ratio
            const cardAspectRatio = cardRect.width / cardRect.height;

            if (youtubeAspectRatio > cardAspectRatio) {{
                // Video is wider than card, fit to width
                containerWidth = cardRect.width;
                containerHeight = Math.round(cardRect.width / youtubeAspectRatio);
            }} else {{
                // Video is taller than card, fit to height
                containerHeight = cardRect.height;
                containerWidth = Math.round(cardRect.height * youtubeAspectRatio);
            }}

            // Apply percentage scaling
            containerWidth = Math.round(containerWidth * (PREVIEW_SIZE_PERCENTAGE / 100));
            containerHeight = Math.round(containerHeight * (PREVIEW_SIZE_PERCENTAGE / 100));

            // Clamp to 90% of viewport, preserving aspect ratio. High % values on small
            // home-section cards otherwise overflow the screen entirely.
            const maxW = window.innerWidth * 0.9;
            const maxH = window.innerHeight * 0.9;
            const clampScale = Math.min(1, maxW / containerWidth, maxH / containerHeight);
            if (clampScale < 1) {{
                containerWidth = Math.round(containerWidth * clampScale);
                containerHeight = Math.round(containerHeight * clampScale);
            }}

            log(`YouTube FitContent dimensions: ${{containerWidth}}x${{containerHeight}} (${{PREVIEW_SIZE_PERCENTAGE}}% of calculated fit)`);
        }} else {{
            // Manual mode uses configured width/height
            containerWidth = PREVIEW_WIDTH;
            containerHeight = PREVIEW_HEIGHT;
        }}

        // Calculate positioning based on positioning mode
        let containerStyles;
        if (PREVIEW_POSITIONING_MODE === 'Center') {{
            containerStyles = `
                position: fixed;
                top: 50%;
                left: 50%;
                transform: translate(-50%, -50%);
                width: ${{containerWidth}}px;
                height: ${{containerHeight}}px;
                border-radius: ${{PREVIEW_BORDER_RADIUS}}px;
                /* Compositor-level clip — overflow:hidden + border-radius
                   isn't enough when the iframe child has transform: scale(),
                   which puts it on its own GPU layer that escapes the
                   parent's rounded clip in Chromium. clip-path forces strict
                   raster clipping so the corners stay round. */
                clip-path: inset(0 round ${{PREVIEW_BORDER_RADIUS}}px);
                overflow: hidden;
                background: #000;
                box-shadow: 0 4px 12px rgba(0,0,0,0.5);
                z-index: 10000;
                pointer-events: none;
                opacity: 0;
                transition: opacity 0.3s ease;
            `;
        }} else if (PREVIEW_POSITIONING_MODE === 'AnchorToCard') {{
            // Anchor to card: position via translate3d and update each frame in
            // attachAnchorTracker so the preview follows the card on scroll.
            // PREVIEW_OFFSET_X/Y apply as a delta from the card center.
            const rawX = Math.round(cardRect.left + cardRect.width / 2 - containerWidth / 2 + PREVIEW_OFFSET_X);
            const rawY = Math.round(cardRect.top + cardRect.height / 2 - containerHeight / 2 + PREVIEW_OFFSET_Y);
            // Pin to Viewport keeps the preview fully on-screen by clamping
            // the anchor; otherwise honour the literal card-tethered position
            // (preview can clip at edges and scrolls off with its card).
            const pinned = ENABLE_ANCHOR_PIN_TO_VIEWPORT
                ? clampAnchorToViewport(rawX, rawY, containerWidth, containerHeight)
                : {{ x: rawX, y: rawY }};
            const anchorX = pinned.x;
            const anchorY = pinned.y;
            containerStyles = `
                position: fixed;
                top: ${{anchorY}}px;
                left: ${{anchorX}}px;
                width: ${{containerWidth}}px;
                height: ${{containerHeight}}px;
                border-radius: ${{PREVIEW_BORDER_RADIUS}}px;
                /* Compositor-level clip — overflow:hidden + border-radius
                   isn't enough when the iframe child has transform: scale(),
                   which puts it on its own GPU layer that escapes the
                   parent's rounded clip in Chromium. clip-path forces strict
                   raster clipping so the corners stay round.
                   Also: positioning via top/left here (instead of
                   transform: translate3d) is a Chromium workaround — any
                   transform on this container makes its own bottom-left
                   corner refuse to clip, regardless of border-radius /
                   clip-path / overflow:hidden combination. */
                clip-path: inset(0 round ${{PREVIEW_BORDER_RADIUS}}px);
                overflow: hidden;
                background: #000;
                box-shadow: 0 4px 12px rgba(0,0,0,0.5);
                z-index: 10000;
                pointer-events: none;
                opacity: 0;
                transition: opacity 0.3s ease;
            `;
        }} else {{
            containerStyles = `
                position: fixed;
                top: calc(${{cardCenterY}}px + ${{PREVIEW_OFFSET_Y}}px);
                left: calc(${{cardCenterX}}px + ${{PREVIEW_OFFSET_X}}px);
                transform: translate(-50%, -50%);
                width: ${{containerWidth}}px;
                height: ${{containerHeight}}px;
                border-radius: ${{PREVIEW_BORDER_RADIUS}}px;
                /* Compositor-level clip — overflow:hidden + border-radius
                   isn't enough when the iframe child has transform: scale(),
                   which puts it on its own GPU layer that escapes the
                   parent's rounded clip in Chromium. clip-path forces strict
                   raster clipping so the corners stay round. */
                clip-path: inset(0 round ${{PREVIEW_BORDER_RADIUS}}px);
                overflow: hidden;
                background: #000;
                box-shadow: 0 4px 12px rgba(0,0,0,0.5);
                z-index: 10000;
                pointer-events: none;
                opacity: 0;
                transition: opacity 0.3s ease;
            `;
        }}

        container.style.cssText = containerStyles;

        // Configure iframe for YouTube with proper attributes to prevent Error 153.
        // The iframe element itself is rendered at NATIVE_IFRAME_DIMS (e.g.
        // 1920×1080 for 'adaptive' / hd1080) and CSS-scaled down to fit the
        // container. This forces YouTube's adaptive bitrate selector to serve
        // HD because it reads the iframe's pixel dimensions. transform-origin
        // 0 0 keeps the scaled box anchored at top-left so it covers the
        // container exactly with no sub-pixel sliver (Bug 2 fix preserved).
        const scaleX = containerWidth / NATIVE_IFRAME_DIMS.w;
        const scaleY = containerHeight / NATIVE_IFRAME_DIMS.h;
        // clip-path on the container fails to clip the iframe at extreme
        // scale ratios (e.g. hd2160 = 3840×2160 native scaled to ~640px) —
        // Chromium's compositor renders the iframe in its own GPU layer
        // that escapes the parent's rounded clip. Applying clip-path on
        // the IFRAME ITSELF in pre-transform coordinates fixes this. The
        // radius must be scaled up by 1/scale so it lands at the user-set
        // PREVIEW_BORDER_RADIUS after the transform.
        const iframeClipRadius = Math.round(PREVIEW_BORDER_RADIUS / scaleX);
        iframe.style.cssText = `
            position: absolute;
            top: 0;
            left: 0;
            width: ${{NATIVE_IFRAME_DIMS.w}}px;
            height: ${{NATIVE_IFRAME_DIMS.h}}px;
            border: none;
            transform: scale(${{scaleX}}, ${{scaleY}});
            transform-origin: 0 0;
            clip-path: inset(0 round ${{iframeClipRadius}}px);
        `;
        iframe.width = NATIVE_IFRAME_DIMS.w;
        iframe.height = NATIVE_IFRAME_DIMS.h;
        // Set permission and security attributes BEFORE src so the initial
        // navigation commits with the Permissions Policy in effect (issue #16:
        // setting src first caused the first-hover autoplay on a fresh page
        // to be blocked because the iframe's allow='autoplay' delegation
        // wasn't applied when Chrome/Safari committed the navigation).
        iframe.id = 'youtube-preview-' + Date.now();
        iframe.allow = 'accelerometer; autoplay; clipboard-write; encrypted-media; fullscreen; gyroscope; picture-in-picture';
        iframe.setAttribute('allowfullscreen', '');
        iframe.setAttribute('referrerpolicy', 'strict-origin-when-cross-origin');
        iframe.setAttribute('frameborder', '0');
        iframe.dataset.pendingSrc = embedUrl;

        container.appendChild(iframe);

        // Always-on JS loop handler (replaces YouTube's loop=playlist
        // mechanism). Subscribes to playerInfo on iframe load and restarts
        // playback when the video reaches the end. Cleanup on hidePreview.
        const cleanupLoop = attachYouTubeLoopHandler(iframe);
        iframe.addEventListener('load', () => {{
            ytSubscribe(iframe);
            setTimeout(() => ytSubscribe(iframe), 500);
            setTimeout(() => ytSubscribe(iframe), 1500);
        }});
        container._htLoopCleanup = cleanupLoop;

        // Trailer controls overlay (issue #18). Server-side gating already
        // disabled this when persistent preview is off, so we don't re-check.
        if (ENABLE_TRAILER_CONTROLS) {{
            attachTrailerControls(container, iframe);
        }}

        log('Created YouTube preview iframe (src deferred until DOM attach):', embedUrl);

        // Set up YouTube IFrame API for volume and quality control
        // Since video starts muted, immediately unmute and set volume when ready
        iframe.addEventListener('load', () => {{
            // Use shorter delay since we're just setting volume and quality, not waiting for autoplay
            setTimeout(() => {{
                try {{
                    const volumePercent = ENABLE_PREVIEW_AUDIO ? PREVIEW_VOLUME : 0;

                    // Set playback quality using IFrame API (2025 method)
                    if (REMOTE_VIDEO_QUALITY !== 'adaptive') {{
                        iframe.contentWindow.postMessage(JSON.stringify({{
                            event: 'command',
                            func: 'setPlaybackQuality',
                            args: [REMOTE_VIDEO_QUALITY]
                        }}), '*');
                        log('YouTube quality set to: ' + REMOTE_VIDEO_QUALITY);
                    }}

                    // Issue #16: Chrome/Safari pause a playing muted video when
                    // postMessage unMute fires without prior user activation,
                    // so keep muted until the document has sticky activation.
                    const hasUserActivation = !!(navigator.userActivation && navigator.userActivation.hasBeenActive);
                    if (volumePercent === 0 || !hasUserActivation) {{
                        log('YouTube iframe kept muted (volume=0, audio disabled, or no user activation yet)');
                    }} else {{
                        iframe.contentWindow.postMessage(JSON.stringify({{event:'command',func:'unMute',args:''}}), '*');
                        iframe.contentWindow.postMessage(JSON.stringify({{event:'command',func:'setVolume',args:[volumePercent]}}), '*');
                        log('YouTube iframe unmuted and volume set to ' + volumePercent + '%');
                    }}
                }} catch (e) {{
                    log('Error setting YouTube volume/quality:', e);
                }}
            }}, 100); // Minimal delay for API readiness
        }});

        return container;
    }}

    function createVideoPreview(trailerPath, cardElement) {{
        // Create container div for the video
        const container = document.createElement('div');
        const video = document.createElement('video');

        // Get card position relative to viewport (needed for custom positioning)
        const cardRect = cardElement.getBoundingClientRect();
        const cardCenterX = cardRect.left + cardRect.width / 2;
        const cardCenterY = cardRect.top + cardRect.height / 2;

        // Calculate container size based on sizing mode
        let containerWidth, containerHeight;
        if (PREVIEW_SIZING_MODE === 'FitContent') {{
            // For fit content mode, start with card dimensions, will be adjusted when video loads
            containerWidth = cardRect.width;
            containerHeight = cardRect.height;
        }} else {{
            // Manual mode uses configured width/height
            containerWidth = PREVIEW_WIDTH;
            containerHeight = PREVIEW_HEIGHT;
        }}

        // Calculate positioning based on positioning mode
        let containerStyles;
        if (PREVIEW_POSITIONING_MODE === 'Center') {{
            // Center the preview in the viewport
            containerStyles = `
                position: fixed;
                top: 50%;
                left: 50%;
                transform: translate(-50%, -50%);
                width: ${{containerWidth}}px;
                height: ${{containerHeight}}px;
                border-radius: ${{PREVIEW_BORDER_RADIUS}}px;
                /* Compositor-level clip — overflow:hidden + border-radius
                   isn't enough when the iframe child has transform: scale(),
                   which puts it on its own GPU layer that escapes the
                   parent's rounded clip in Chromium. clip-path forces strict
                   raster clipping so the corners stay round. */
                clip-path: inset(0 round ${{PREVIEW_BORDER_RADIUS}}px);
                overflow: hidden;
                background: #000;
                box-shadow: 0 4px 12px rgba(0,0,0,0.5);
                z-index: 10000;
                pointer-events: none;
                opacity: 0;
                transition: opacity 0.3s ease;
            `;
        }} else if (PREVIEW_POSITIONING_MODE === 'AnchorToCard') {{
            // Anchor to card: position via translate3d and update each frame in
            // attachAnchorTracker so the preview follows the card on scroll.
            // PREVIEW_OFFSET_X/Y apply as a delta from the card center.
            const rawX = Math.round(cardRect.left + cardRect.width / 2 - containerWidth / 2 + PREVIEW_OFFSET_X);
            const rawY = Math.round(cardRect.top + cardRect.height / 2 - containerHeight / 2 + PREVIEW_OFFSET_Y);
            // Pin to Viewport keeps the preview fully on-screen by clamping
            // the anchor; otherwise honour the literal card-tethered position
            // (preview can clip at edges and scrolls off with its card).
            const pinned = ENABLE_ANCHOR_PIN_TO_VIEWPORT
                ? clampAnchorToViewport(rawX, rawY, containerWidth, containerHeight)
                : {{ x: rawX, y: rawY }};
            const anchorX = pinned.x;
            const anchorY = pinned.y;
            containerStyles = `
                position: fixed;
                top: ${{anchorY}}px;
                left: ${{anchorX}}px;
                width: ${{containerWidth}}px;
                height: ${{containerHeight}}px;
                border-radius: ${{PREVIEW_BORDER_RADIUS}}px;
                /* Compositor-level clip — overflow:hidden + border-radius
                   isn't enough when the iframe child has transform: scale(),
                   which puts it on its own GPU layer that escapes the
                   parent's rounded clip in Chromium. clip-path forces strict
                   raster clipping so the corners stay round.
                   Also: positioning via top/left here (instead of
                   transform: translate3d) is a Chromium workaround — any
                   transform on this container makes its own bottom-left
                   corner refuse to clip, regardless of border-radius /
                   clip-path / overflow:hidden combination. */
                clip-path: inset(0 round ${{PREVIEW_BORDER_RADIUS}}px);
                overflow: hidden;
                background: #000;
                box-shadow: 0 4px 12px rgba(0,0,0,0.5);
                z-index: 10000;
                pointer-events: none;
                opacity: 0;
                transition: opacity 0.3s ease;
            `;
        }} else {{
            // Custom positioning relative to card with offsets
            containerStyles = `
                position: fixed;
                top: calc(${{cardCenterY}}px + ${{PREVIEW_OFFSET_Y}}px);
                left: calc(${{cardCenterX}}px + ${{PREVIEW_OFFSET_X}}px);
                transform: translate(-50%, -50%);
                width: ${{containerWidth}}px;
                height: ${{containerHeight}}px;
                border-radius: ${{PREVIEW_BORDER_RADIUS}}px;
                /* Compositor-level clip — overflow:hidden + border-radius
                   isn't enough when the iframe child has transform: scale(),
                   which puts it on its own GPU layer that escapes the
                   parent's rounded clip in Chromium. clip-path forces strict
                   raster clipping so the corners stay round. */
                clip-path: inset(0 round ${{PREVIEW_BORDER_RADIUS}}px);
                overflow: hidden;
                background: #000;
                box-shadow: 0 4px 12px rgba(0,0,0,0.5);
                z-index: 10000;
                pointer-events: none;
                opacity: 0;
                transition: opacity 0.3s ease;
            `;
        }}

        // Apply the container styles
        container.style.cssText = containerStyles;

        // Style the video to fill the container
        video.style.cssText = `
            width: 100%;
            height: 100%;
            object-fit: cover;
        `;

        video.src = trailerPath;
        video.muted = !ENABLE_PREVIEW_AUDIO;
        video.loop = true;
        video.preload = 'metadata';

        // Set volume based on configuration (0-100 range converted to 0.0-1.0)
        if (ENABLE_PREVIEW_AUDIO) {{
            video.volume = PREVIEW_VOLUME / 100.0;
        }}

        // Append video to container
        container.appendChild(video);

        return container;
    }}

    // ── Trailer controls (issue #18): play/pause, seek, volume, fullscreen ──
    // Icon set — inline SVG so we don't add an external dependency. Each
    // path is the standard Material-Symbols play / pause / volume / fullscreen
    // glyph, simplified for inline use.
    const ICON_PLAY     = `<svg viewBox=""0 0 24 24""><path d=""M8 5v14l11-7z"" fill=""currentColor""/></svg>`;
    const ICON_PAUSE    = `<svg viewBox=""0 0 24 24""><path d=""M6 19h4V5H6v14zm8-14v14h4V5h-4z"" fill=""currentColor""/></svg>`;
    const ICON_VOL      = `<svg viewBox=""0 0 24 24""><path d=""M3 9v6h4l5 5V4L7 9H3zm13.5 3c0-1.77-1.02-3.29-2.5-4.03v8.05c1.48-.73 2.5-2.25 2.5-4.02zM14 3.23v2.06c2.89.86 5 3.54 5 6.71s-2.11 5.85-5 6.71v2.06c4.01-.91 7-4.49 7-8.77s-2.99-7.86-7-8.77z"" fill=""currentColor""/></svg>`;
    const ICON_VOL_MUTE = `<svg viewBox=""0 0 24 24""><path d=""M16.5 12c0-1.77-1.02-3.29-2.5-4.03v2.21l2.45 2.45c.03-.2.05-.41.05-.63zm2.5 0c0 .94-.2 1.82-.54 2.64l1.51 1.51C20.63 14.91 21 13.5 21 12c0-4.28-2.99-7.86-7-8.77v2.06c2.89.86 5 3.54 5 6.71zM4.27 3L3 4.27 7.73 9H3v6h4l5 5v-6.73l4.25 4.25c-.67.52-1.42.93-2.25 1.18v2.06c1.38-.31 2.63-.95 3.69-1.81L19.73 21 21 19.73l-9-9L4.27 3zM12 4L9.91 6.09 12 8.18V4z"" fill=""currentColor""/></svg>`;
    const ICON_FS_ENTER = `<svg viewBox=""0 0 24 24""><path d=""M7 14H5v5h5v-2H7v-3zm-2-4h2V7h3V5H5v5zm12 7h-3v2h5v-5h-2v3zM14 5v2h3v3h2V5h-5z"" fill=""currentColor""/></svg>`;
    const ICON_FS_EXIT  = `<svg viewBox=""0 0 24 24""><path d=""M5 16h3v3h2v-5H5v2zm3-8H5v2h5V5H8v3zm6 11h2v-3h3v-2h-5v5zm2-11V5h-2v5h5V8h-3z"" fill=""currentColor""/></svg>`;

    function fmtTime(secs) {{
        if (!isFinite(secs) || secs < 0) return '0:00';
        const m = Math.floor(secs / 60);
        const s = Math.floor(secs % 60).toString().padStart(2, '0');
        return m + ':' + s;
    }}

    // Send a YouTube IFrame Player API command via postMessage.
    // youtube-nocookie's player listens on its own contentWindow when
    // enablejsapi=1 is in the embed URL.
    function ytCommand(iframe, func, args) {{
        if (!iframe || !iframe.contentWindow) return;
        try {{
            iframe.contentWindow.postMessage(JSON.stringify({{
                event: 'command',
                func: func,
                args: args || []
            }}), '*');
        }} catch (e) {{
            log('ytCommand failed:', func, e);
        }}
    }}

    // Manual single-video loop handler. Runs unconditionally for all
    // YouTube previews (controls or not). Subscribes to playerInfo and
    // restarts the video when it ends (playerState === 0). Replaces the
    // legacy &loop=1&playlist=... URL params that triggered YouTube's
    // playlist navigation UI (prev/pause/next icons in the centre).
    // Returns a cleanup function for hidePreview.
    function attachYouTubeLoopHandler(iframe) {{
        const onMessage = (e) => {{
            if (!e.origin || e.origin.indexOf('youtube') === -1) return;
            if (e.source !== iframe.contentWindow) return;
            let data;
            try {{ data = JSON.parse(e.data); }} catch (_) {{ return; }}
            if (data.event !== 'infoDelivery' || !data.info) return;
            if (data.info.playerState === 0) {{
                ytCommand(iframe, 'seekTo', [0, true]);
                ytCommand(iframe, 'playVideo');
            }}
        }};
        window.addEventListener('message', onMessage);
        return () => window.removeEventListener('message', onMessage);
    }}

    // Subscribe to the YouTube playerInfo channel — the iframe will then
    // post 'infoDelivery' messages with currentTime/duration/playerState/
    // volume/muted/playbackQuality. YouTube sometimes ignores the very first
    // listening hint, so callers retry a few times after iframe load.
    function ytSubscribe(iframe) {{
        if (!iframe || !iframe.contentWindow) return;
        try {{
            iframe.contentWindow.postMessage(JSON.stringify({{
                event: 'listening',
                id: iframe.id,
                channel: 'playerInfo'
            }}), '*');
        }} catch (e) {{
            log('ytSubscribe failed:', e);
        }}
    }}

    // Build the controls bar inside the YouTube preview container, wire all
    // user interactions, subscribe to player state. Stores a cleanup
    // function on container._htControlsCleanup for hidePreview to call.
    function attachTrailerControls(container, iframe) {{
        // Invisible click absorber over the iframe — keeps clicks/taps from
        // reaching YouTube's player so its native tap-feedback overlay
        // (centre skip-back / play-pause / skip-forward icons) doesn't fire.
        // Mouse hover still drives controls visibility.
        const mask = document.createElement('div');
        mask.className = 'ht-iframe-mask';
        mask.addEventListener('click', (e) => e.stopPropagation());
        container.appendChild(mask);

        const controls = document.createElement('div');
        controls.className = 'ht-controls';
        // No background here — the gradient is painted on a dedicated
        // child div (.ht-controls-bg) below. Reason: when the controls
        // bar paints its OWN background, the bottom-left corner refuses
        // to clip in Chromium as soon as a form element (range input)
        // inside the bar gets hovered. Painting the bg on a sibling-less
        // child whose only job is the gradient sidesteps the bug.
        // innerHTML is safer than chained createElement here — strings are all
        // static, no user content interpolated.
        // Background div — same gradient as before, with its own clip-path
        // that handles the bottom corners. This is what survives the
        // Chromium hover-clip bug.
        const controlsBg = document.createElement('div');
        controlsBg.className = 'ht-controls-bg';
        controlsBg.style.clipPath = 'inset(0 round 0 0 ' + PREVIEW_BORDER_RADIUS + 'px ' + PREVIEW_BORDER_RADIUS + 'px)';
        controls.appendChild(controlsBg);

        controls.innerHTML +=
            '<button class=""ht-control-btn ht-play-pause"" type=""button"" aria-label=""Play/pause"">' + ICON_PAUSE + '</button>' +
            '<span class=""ht-time ht-current"">0:00</span>' +
            '<input type=""range"" class=""ht-seek"" min=""0"" max=""100"" value=""0"" step=""0.1"" aria-label=""Seek"">' +
            '<span class=""ht-time ht-duration"">0:00</span>' +
            '<div class=""ht-volume-wrap"">' +
            '<button class=""ht-control-btn ht-mute"" type=""button"" aria-label=""Mute/unmute"">' + ICON_VOL_MUTE + '</button>' +
            '<input type=""range"" class=""ht-volume"" min=""0"" max=""100"" value=""' + PREVIEW_VOLUME + '"" aria-label=""Volume"">' +
            '</div>' +
            '<button class=""ht-control-btn ht-fs"" type=""button"" aria-label=""Toggle fullscreen"">' + ICON_FS_ENTER + '</button>';
        container.appendChild(controls);

        const playPauseBtn = controls.querySelector('.ht-play-pause');
        const muteBtn      = controls.querySelector('.ht-mute');
        const fsBtn        = controls.querySelector('.ht-fs');
        const seekEl       = controls.querySelector('.ht-seek');
        const volEl        = controls.querySelector('.ht-volume');
        const curEl        = controls.querySelector('.ht-current');
        const durEl        = controls.querySelector('.ht-duration');

        let isPaused = false;
        let isMuted = true; // existing code starts muted until user activation
        let isSeeking = false;
        let lastUserVolumeAt = 0;
        // Track whether the cursor is currently over any part of the preview
        // so the auto-reveal-then-hide sequence below doesn't dismiss the
        // bar out from under a user who's already hovering it (especially
        // on first hover, which lands inside the 2800 ms window).
        let mouseInPreview = false;

        // Auto-show controls on hover, fade out 350ms after mouse leaves
        let hideTimer = null;
        function showCtrls() {{
            if (hideTimer) {{ clearTimeout(hideTimer); hideTimer = null; }}
            controls.classList.add('visible');
        }}
        function hideCtrlsLater() {{
            if (document.fullscreenElement) return; // FS uses :hover via CSS
            if (mouseInPreview) return;             // user is interacting — don't hide
            if (hideTimer) clearTimeout(hideTimer);
            hideTimer = setTimeout(() => controls.classList.remove('visible'), 350);
        }}
        function onPreviewEnter() {{ mouseInPreview = true; showCtrls(); }}
        function onPreviewLeave() {{ mouseInPreview = false; hideCtrlsLater(); }}
        // mouseenter/leave on the mask + controls — iframe events don't
        // fire when the click absorber sits above it.
        mask.addEventListener('mouseenter', onPreviewEnter);
        mask.addEventListener('mouseleave', onPreviewLeave);
        iframe.addEventListener('mouseenter', onPreviewEnter);
        iframe.addEventListener('mouseleave', onPreviewLeave);
        controls.addEventListener('mouseenter', onPreviewEnter);
        controls.addEventListener('mouseleave', onPreviewLeave);
        // Brief reveal when the preview first appears so users discover the
        // bar. The auto-hide at 2800 ms only fires if the cursor isn't
        // currently inside the preview — otherwise the user would lose the
        // bar mid-interaction.
        setTimeout(showCtrls, 800);
        setTimeout(hideCtrlsLater, 2800);

        playPauseBtn.addEventListener('click', (e) => {{
            e.stopPropagation();
            ytCommand(iframe, isPaused ? 'playVideo' : 'pauseVideo');
        }});
        muteBtn.addEventListener('click', (e) => {{
            e.stopPropagation();
            ytCommand(iframe, isMuted ? 'unMute' : 'mute');
        }});
        fsBtn.addEventListener('click', (e) => {{
            e.stopPropagation();
            if (document.fullscreenElement) {{
                document.exitFullscreen();
            }} else {{
                // requestFullscreen on the container (not the iframe) so the
                // controls bar stays inside the FS surface — the iframe is
                // resized to fill via the :fullscreen CSS rule.
                const req = container.requestFullscreen
                    ? container.requestFullscreen.bind(container)
                    : (container.webkitRequestFullscreen
                        ? container.webkitRequestFullscreen.bind(container)
                        : null);
                if (req) req().catch(err => log('FS request failed:', err));
            }}
        }});

        seekEl.addEventListener('pointerdown', () => {{ isSeeking = true; }});
        seekEl.addEventListener('input', (e) => {{
            e.stopPropagation();
            if (isSeeking) curEl.textContent = fmtTime(parseFloat(seekEl.value));
        }});
        seekEl.addEventListener('change', (e) => {{
            e.stopPropagation();
            const t = parseFloat(seekEl.value);
            ytCommand(iframe, 'seekTo', [t, true]);
            // Brief delay so the next infoDelivery doesn't snap the slider back
            setTimeout(() => {{ isSeeking = false; }}, 100);
        }});
        seekEl.addEventListener('click', (e) => e.stopPropagation());

        volEl.addEventListener('input', (e) => {{
            e.stopPropagation();
            const v = parseInt(volEl.value, 10);
            lastUserVolumeAt = Date.now();
            if (v > 0 && isMuted) ytCommand(iframe, 'unMute');
            ytCommand(iframe, 'setVolume', [v]);
        }});
        volEl.addEventListener('click', (e) => e.stopPropagation());

        // Receive player info: state, time, volume, muted
        const onMessage = (e) => {{
            if (!e.origin || e.origin.indexOf('youtube') === -1) return;
            if (e.source !== iframe.contentWindow) return;
            let data;
            try {{ data = JSON.parse(e.data); }} catch (_) {{ return; }}
            if (data.event !== 'infoDelivery' || !data.info) return;
            const info = data.info;
            if (info.playerState !== undefined) {{
                const playing = (info.playerState === 1);
                isPaused = !playing;
                playPauseBtn.innerHTML = isPaused ? ICON_PLAY : ICON_PAUSE;
            }}
            if (info.duration !== undefined && info.duration > 0) {{
                if (parseFloat(seekEl.max) !== info.duration) seekEl.max = info.duration;
                durEl.textContent = fmtTime(info.duration);
            }}
            if (info.currentTime !== undefined && !isSeeking) {{
                seekEl.value = info.currentTime;
                curEl.textContent = fmtTime(info.currentTime);
            }}
            if (info.muted !== undefined) {{
                isMuted = !!info.muted;
                muteBtn.innerHTML = isMuted ? ICON_VOL_MUTE : ICON_VOL;
            }}
            if (info.volume !== undefined && Date.now() - lastUserVolumeAt > 500) {{
                if (parseInt(volEl.value, 10) !== info.volume) volEl.value = info.volume;
            }}
        }};
        window.addEventListener('message', onMessage);

        // Track fullscreen state — toggle FS icon, suppress one Escape after
        // exit so the persistent dismisser doesn't tear down the preview on
        // the same Escape press, and set recentFsExit to keep the resize
        // event that fires next from doing the same.
        const onFsChange = () => {{
            const isFs = !!document.fullscreenElement;
            fsBtn.innerHTML = isFs ? ICON_FS_EXIT : ICON_FS_ENTER;
            if (!isFs) {{
                suppressEscapeOnce = true;
                setTimeout(() => {{ suppressEscapeOnce = false; }}, 250);
                recentFsExit = true;
                setTimeout(() => {{ recentFsExit = false; }}, 600);
            }}
        }};
        document.addEventListener('fullscreenchange', onFsChange);

        // Subscribe to playerInfo channel after iframe loads. YouTube
        // sometimes drops the first listening hint — retry over the next
        // few seconds to be safe.
        const trySub = () => ytSubscribe(iframe);
        iframe.addEventListener('load', () => {{
            trySub();
            setTimeout(trySub, 500);
            setTimeout(trySub, 1500);
            setTimeout(trySub, 3500);
        }});

        // Cleanup hook called by hidePreview
        container._htControlsCleanup = () => {{
            window.removeEventListener('message', onMessage);
            document.removeEventListener('fullscreenchange', onFsChange);
            if (hideTimer) {{ clearTimeout(hideTimer); hideTimer = null; }}
            if (document.fullscreenElement === container) {{
                document.exitFullscreen().catch(() => {{}});
            }}
        }};
    }}

    // AnchorToCard positioning: re-read the card's bounding rect every frame
    // and update the preview's translate3d so it tracks the card during scroll.
    // The loop self-terminates when the preview is torn down or the card
    // leaves the DOM. hidePreview() also cancels the rAF handle explicitly.
    function attachAnchorTracker(container, cardElement) {{
        // Core update: reads card rect and writes one transform string. Cheap.
        function update() {{
            if (currentPreview !== container || !cardElement.isConnected) return false;
            const cardRect = cardElement.getBoundingClientRect();
            const width = container.offsetWidth;
            const height = container.offsetHeight;
            const rawX = Math.round(cardRect.left + cardRect.width / 2 - width / 2 + PREVIEW_OFFSET_X);
            const rawY = Math.round(cardRect.top + cardRect.height / 2 - height / 2 + PREVIEW_OFFSET_Y);
            const pinned = ENABLE_ANCHOR_PIN_TO_VIEWPORT
                ? clampAnchorToViewport(rawX, rawY, width, height)
                : {{ x: rawX, y: rawY }};
            const anchorX = pinned.x;
            const anchorY = pinned.y;
            // Position via top/left (not transform) — see containerStyles
            // comment in createYouTubePreview: any transform on the
            // container breaks the bottom-left corner clip in Chromium.
            container.style.left = `${{anchorX}}px`;
            container.style.top = `${{anchorY}}px`;
            // Layout shifts (card images loading, scrollbar appearing) reposition
            // the preview off-scroll. The halo's scroll-only listener won't catch
            // these, so drive halo updates from the same frame the preview moves.
            if (BACKGROUND_BLUR_MODE === 'Halo') {{
                const backdrop = document.getElementById('hover-trailer-backdrop');
                if (backdrop) trackHaloPosition(backdrop);
            }}
            return true;
        }}
        function tick() {{
            if (!update()) {{
                anchorTrackerRafId = null;
                return;
            }}
            anchorTrackerRafId = requestAnimationFrame(tick);
        }}
        // Scroll listener (capture-phase so we catch scroll on any inner
        // scrollable ancestor of the card, including Jellyfin's inner scroll
        // containers). Fires in sync with the browser's scroll pipeline, so
        // the transform updates on the same frame the content scrolls —
        // eliminates the visible drag seen when relying on rAF alone.
        anchorScrollHandler = () => {{ update(); }};
        window.addEventListener('scroll', anchorScrollHandler, {{ passive: true, capture: true }});
        anchorTrackerRafId = requestAnimationFrame(tick);
    }}

    // Persistent-preview dismissers: a document-level click or Escape key
    // tears down the current preview. Listeners are attached when the preview
    // shows and removed in hidePreview. The click handler uses capture-phase
    // so cards' own click handlers still fire (they also tear down via
    // isPlaying + hidePreview in the card click listener).
    function attachPersistentDismissers() {{
        detachPersistentDismissers();
        const onClick = (e) => {{
            // Clicks on the preview itself (controls bar, container) must
            // not dismiss — only outside-clicks tear it down.
            if (currentPreview && currentPreview.contains(e.target)) return;
            hidePreview();
        }};
        const onKey = (e) => {{
            if (e.key !== 'Escape') return;
            // Browser handles Escape to exit fullscreen; don't double-fire.
            if (document.fullscreenElement) return;
            // Recently exited FS via Escape → swallow this one keypress
            // (set by the trailer-controls fullscreenchange listener).
            if (suppressEscapeOnce) {{ suppressEscapeOnce = false; return; }}
            hidePreview();
        }};
        document.addEventListener('click', onClick, true);
        document.addEventListener('keydown', onKey, true);
        persistentDismissHandlers = {{ click: onClick, keydown: onKey }};
    }}

    function detachPersistentDismissers() {{
        if (!persistentDismissHandlers) return;
        document.removeEventListener('click', persistentDismissHandlers.click, true);
        document.removeEventListener('keydown', persistentDismissHandlers.keydown, true);
        persistentDismissHandlers = null;
    }}

    function showPreview(element, itemId) {{
        if (isPlaying) return;

        // In persistent mode, a new hover replaces the old preview. Otherwise,
        // an existing preview blocks a new one until it is dismissed.
        if (currentPreview) {{
            if (ENABLE_PERSISTENT_PREVIEW && currentCardElement !== element) {{
                hidePreview();
            }} else {{
                return;
            }}
        }}

        hideProgressIndicator(currentCardElement || element);

        const myGeneration = ++previewGeneration;
        const abortController = new AbortController();
        if (currentAbortController) {{ currentAbortController.abort(); }}
        currentAbortController = abortController;

        log('Showing preview for item:', itemId);

        // Show loading toast
        showToast('Loading trailer...', 'loading');

        // Get trailer info from API
        fetch(`${{API_BASE_URL}}/HoverTrailer/TrailerInfo/${{itemId}}`, {{ signal: abortController.signal }})
            .then(response => {{
                if (!response.ok) {{
                    if (response.status === 404) {{
                        throw new Error('NO_TRAILER');
                    }}
                    throw new Error('Trailer not found');
                }}
                return response.json();
            }})
            .then(trailerInfo => {{
                // Hide loading toast when trailer info received
                hideToast();

                // Check if preview was cancelled during fetch
                if (myGeneration !== previewGeneration || currentPreview || isPlaying) {{
                    log('Preview cancelled during fetch');
                    return;
                }}

                log('Creating video preview for trailer:', trailerInfo.Name);
                log('Trailer info received:', {{
                    id: trailerInfo.Id,
                    name: trailerInfo.Name,
                    path: trailerInfo.Path,
                    isRemote: trailerInfo.IsRemote,
                    trailerType: trailerInfo.TrailerType,
                    source: trailerInfo.Source
                }});

                // Determine video source based on trailer type
                let videoSource;
                if (trailerInfo.IsRemote) {{
                    // For remote YouTube trailers, convert to embed URL
                    const youtubeUrl = trailerInfo.Path;
                    log('Original YouTube URL:', youtubeUrl);

                    // Extract video ID from YouTube URL
                    const videoId = extractYouTubeVideoId(youtubeUrl);
                    if (videoId) {{
                        // Use youtube-nocookie.com to avoid Error 153 and privacy issues
                        // Enable JS API for volume control and quality setting
                        // ALWAYS start muted to prevent loud initial audio, then unmute via API

                        // Append &vq=... when the user pinned a specific tier.
                        // YouTube treats vq as a hint, not a contract, but combined
                        // with the NATIVE_IFRAME_DIMS sizing it nudges the player
                        // toward the requested quality. 'adaptive' is the default
                        // and lets YouTube fully decide based on bandwidth.
                        const vqParam = REMOTE_VIDEO_QUALITY !== 'adaptive'
                            ? `&vq=${{REMOTE_VIDEO_QUALITY}}`
                            : '';
                        // No &loop=1&playlist=... — that combo triggers
                        // YouTube's playlist navigation UI (prev/pause/next
                        // icons in the centre of the video) every time the
                        // single-video playlist boundary is hit. Loop is
                        // handled manually in attachYouTubeLoopHandler:
                        // when playerState === 0 (ENDED) we seekTo(0) +
                        // playVideo, which avoids the playlist UI entirely.
                        videoSource = `https://www.youtube-nocookie.com/embed/${{videoId}}?` +
                            `autoplay=1` +
                            `&mute=1` +                  // Always start muted to prevent loud audio spike
                            `&controls=0` +
                            `&playsinline=1` +           // Mobile compatibility
                            `&rel=0` +                   // No related videos
                            `&modestbranding=1` +        // Minimal branding
                            `&enablejsapi=1` +           // Enable JS API for volume and quality control
                            `&disablekb=1` +             // Suppress YouTube keyboard shortcut UI feedback
                            `&iv_load_policy=3` +        // No annotations / info cards
                            `&fs=0` +                    // No native fullscreen button (we provide our own)
                            vqParam;
                        log('Converted to YouTube nocookie embed URL:', videoSource);
                        log('Using youtube-nocookie.com to prevent Error 153');
                        log('YouTube quality: ' + REMOTE_VIDEO_QUALITY + ', Volume: ' + PREVIEW_VOLUME + '%');
                    }} else {{
                        log('Failed to extract YouTube video ID from:', youtubeUrl);
                        throw new Error('Invalid YouTube URL format');
                    }}
                }} else {{
                    // For local trailers, use Jellyfin's stream endpoint
                    videoSource = `${{API_BASE_URL}}/Videos/${{trailerInfo.Id}}/stream`;
                    log('Using Jellyfin stream endpoint for local trailer:', videoSource);
                }}

                const container = trailerInfo.IsRemote
                    ? createYouTubePreview(videoSource, element)
                    : createVideoPreview(videoSource, element);
                const video = container.querySelector(trailerInfo.IsRemote ? 'iframe' : 'video');

                // Set current preview and card element BEFORE registering event listeners
                // to avoid race conditions
                document.body.appendChild(container);
                currentPreview = container;
                currentCardElement = element;

                // AnchorToCard mode: start the rAF loop that keeps the preview
                // tethered to the card as the user scrolls. Runs for both
                // YouTube and local-video paths.
                if (PREVIEW_POSITIONING_MODE === 'AnchorToCard') {{
                    attachAnchorTracker(container, element);
                }}

                // Persistent preview: install document-level listeners so the
                // user can dismiss via click or Escape while the cursor is off
                // the card.
                if (ENABLE_PERSISTENT_PREVIEW) {{
                    attachPersistentDismissers();
                }}

                // Issue #16: assign YouTube iframe src AFTER the iframe is
                // attached to the live document so Permissions Policy
                // ('allow=autoplay') is in effect when Chrome/Safari commit
                // the navigation. src was stashed in createYouTubePreview().
                if (trailerInfo.IsRemote && video && video.dataset.pendingSrc) {{
                    const pending = video.dataset.pendingSrc;
                    delete video.dataset.pendingSrc;
                    video.src = pending;
                }}

                if (trailerInfo.IsRemote) {{
                    // For YouTube iframe, show immediately and apply blur
                    log('YouTube iframe loaded, showing preview');
                    setTimeout(() => {{
                        if (currentPreview) {{
                            container.style.opacity = PREVIEW_OPACITY;
                            applyBackgroundBlur();
                            log('YouTube preview visible');
                        }}
                    }}, 500); // Give iframe time to start loading
                }} else {{
                    // For local video, wait for loadeddata event
                    video.addEventListener('loadeddata', () => {{
                        if (!currentPreview) {{
                            log('Preview was cancelled before loadeddata');
                            return;
                        }}
                        log('Local video loadeddata event fired');
                        container.style.opacity = PREVIEW_OPACITY;
                        applyBackgroundBlur();
                        video.play().catch(e => {{
                            log('Error playing local video:', e.name + ': ' + e.message);

                            if (e.name === 'NotAllowedError' && !video.muted) {{
                                log('Audio not allowed, falling back to muted playback');
                                video.muted = true;
                                video.play().catch(muteError => {{
                                    log('Even muted playback failed:', muteError.name + ': ' + muteError.message);
                                }});
                            }}
                        }});
                    }});
                }}

                // Handle video metadata load for fit content mode (only for local videos, not YouTube iframes)
                if (PREVIEW_SIZING_MODE === 'FitContent' && !trailerInfo.IsRemote) {{
                    video.addEventListener('loadedmetadata', () => {{
                        try {{
                            log('Video metadata loaded for FitContent mode');
                            // Check if preview is still active and card element exists
                            if (!currentPreview || !currentCardElement) {{
                                log('Preview was cancelled before loadedmetadata, skipping resize');
                                return;
                            }}

                            const cardRect = currentCardElement.getBoundingClientRect();
                            const videoAspectRatio = video.videoWidth / video.videoHeight;
                            const cardAspectRatio = cardRect.width / cardRect.height;

                            log(`Video dimensions: ${{video.videoWidth}}x${{video.videoHeight}} (aspect: ${{videoAspectRatio.toFixed(2)}})`);
                            log(`Card dimensions: ${{cardRect.width}}x${{cardRect.height}} (aspect: ${{cardAspectRatio.toFixed(2)}})`);

                            let newWidth, newHeight;
                            if (videoAspectRatio > cardAspectRatio) {{
                                // Video is wider than card, fit to width
                                newWidth = cardRect.width;
                                newHeight = Math.round(cardRect.width / videoAspectRatio);
                            }} else {{
                                // Video is taller than card, fit to height
                                newHeight = cardRect.height;
                                newWidth = Math.round(cardRect.height * videoAspectRatio);
                            }}

                            log(`Calculated fit dimensions: ${{newWidth}}x${{newHeight}}`);

                            // Apply percentage scaling to the fit dimensions
                            let scaledWidth = Math.round(newWidth * (PREVIEW_SIZE_PERCENTAGE / 100));
                            let scaledHeight = Math.round(newHeight * (PREVIEW_SIZE_PERCENTAGE / 100));

                            // Clamp to 90% of viewport, preserving aspect ratio.
                            const maxW = window.innerWidth * 0.9;
                            const maxH = window.innerHeight * 0.9;
                            const clampScale = Math.min(1, maxW / scaledWidth, maxH / scaledHeight);
                            if (clampScale < 1) {{
                                scaledWidth = Math.round(scaledWidth * clampScale);
                                scaledHeight = Math.round(scaledHeight * clampScale);
                            }}

                            container.style.width = scaledWidth + 'px';
                            container.style.height = scaledHeight + 'px';
                            log(`Applied ${{PREVIEW_SIZE_PERCENTAGE}}% scaling: ${{scaledWidth}}x${{scaledHeight}}`);
                            log('Adjusted container for fit content mode:', scaledWidth + 'x' + scaledHeight);
                        }} catch (error) {{
                            console.error('Error in FitContent loadedmetadata handler:', error);
                            log(`FitContent error: ${{error.message}}`);
                        }}
                    }});
                }}

                // Dismiss the preview on window resize. Trying to live-reposition
                // in AnchorToCard + Halo modes fights the rAF tracker and leaves
                // the halo mask stranded at old coords; a clean tear-down is what
                // the user asked for. The listener is removed in hidePreview.
                // Skip during fullscreen entry/exit — those fire resize but the
                // preview should obviously stay (issue #18).
                resizeHandler = () => {{
                    if (document.fullscreenElement) return;
                    if (recentFsExit) return; // exit-FS resize cleanup, not a real viewport change
                    hidePreview();
                }};
                window.addEventListener('resize', resizeHandler);

                log('Preview created for:', trailerInfo.Name);
            }})
            .catch(error => {{
                if (error.name === 'AbortError') return;
                log('Error loading trailer:', error);
                if (error.message === 'NO_TRAILER') {{
                    showToast('No trailer found', 'error', 3000);
                }} else {{
                    showToast('Error loading trailer', 'error', 3000);
                }}
            }});
    }}

    function hidePreview() {{
        // Invalidate any in-flight fetch and abort it
        previewGeneration++;
        if (currentAbortController) {{
            currentAbortController.abort();
            currentAbortController = null;
        }}

        // Cancel the AnchorToCard rAF tracker BEFORE removing the preview
        // from the DOM, so the next frame's getBoundingClientRect on a stale
        // card/container doesn't fire.
        if (anchorTrackerRafId !== null) {{
            cancelAnimationFrame(anchorTrackerRafId);
            anchorTrackerRafId = null;
        }}
        if (anchorScrollHandler !== null) {{
            window.removeEventListener('scroll', anchorScrollHandler, {{ capture: true }});
            anchorScrollHandler = null;
        }}

        // Remove persistent-preview dismissers if any were installed.
        detachPersistentDismissers();

        // Always hide toast when hiding preview
        hideToast();

        // Always remove any leaked backdrop
        removeBackgroundBlur();

        if (currentPreview) {{
            log('Hiding preview');
            const previewToRemove = currentPreview;
            const videoToStop = previewToRemove.querySelector('video');
            const iframeToStop = previewToRemove.querySelector('iframe');

            // Tear down trailer controls listeners (postMessage, fullscreenchange)
            // and the always-on YouTube loop listener before removing the iframe
            // so we don't keep stale window listeners.
            if (typeof previewToRemove._htControlsCleanup === 'function') {{
                try {{ previewToRemove._htControlsCleanup(); }} catch (e) {{ log('controls cleanup error:', e); }}
            }}
            if (typeof previewToRemove._htLoopCleanup === 'function') {{
                try {{ previewToRemove._htLoopCleanup(); }} catch (e) {{ log('loop cleanup error:', e); }}
            }}

            // Stop video or iframe immediately
            if (videoToStop) {{
                log('Stopping video element');
                videoToStop.pause();
                videoToStop.src = '';
                videoToStop.load();
            }}

            if (iframeToStop) {{
                log('Removing iframe (YouTube)');
                // Don't set iframe.src='about:blank' — iframes are browsing
                // contexts, so that navigation pollutes the parent's session
                // history and breaks the browser Back button (issue #15).
                iframeToStop.remove();
            }}

            // Fade out animation
            currentPreview.style.opacity = '0';

            // Clear references immediately to prevent race conditions
            currentPreview = null;
            currentCardElement = null;

            // Remove resize handler
            if (resizeHandler) {{
                window.removeEventListener('resize', resizeHandler);
                resizeHandler = null;
            }}

            // Remove DOM element after fade animation
            setTimeout(() => {{
                if (previewToRemove && previewToRemove.parentNode) {{
                    previewToRemove.parentNode.removeChild(previewToRemove);
                }}
            }}, 300);

        }}
    }}

    function attachHoverListeners() {{
        // Require .card class to skip Jellyfin's title <a> links (textActionButton),
        // and exclude .chapterCard so scene/chapter buttons on details pages don't
        // fire. Both data-type and data-itemtype variants exist across Jellyfin
        // versions so we accept either.
        const itemCards = document.querySelectorAll('.card[data-type=""Movie""]:not(.chapterCard), .card[data-type=""Series""]:not(.chapterCard), .card[data-itemtype=""Movie""]:not(.chapterCard), .card[data-itemtype=""Series""]:not(.chapterCard)');
        let newCardsCount = 0;

        itemCards.forEach(card => {{
            // Skip if this card element already has listeners attached
            if (attachedCards.has(card)) return;

            const itemId = card.getAttribute('data-id') || card.getAttribute('data-itemid');
            if (!itemId) {{
                log('Warning: Found card without ID');
                return;
            }}

            // Mark this card element as having listeners
            attachedCards.add(card);
            newCardsCount++;

            card.addEventListener('mouseenter', (e) => {{
                if (isPlaying) return;

                clearTimeout(hoverTimeout);
                hoverTimeout = setTimeout(() => {{
                    showPreview(card, itemId);
                }}, HOVER_DELAY);
                showProgressIndicator(card);
            }});

            card.addEventListener('mouseleave', () => {{
                clearTimeout(hoverTimeout);
                hideProgressIndicator(card);
                // In persistent mode, any mouseleave keeps the existing preview
                // alive — even on a different card the user briefly hovered
                // before reaching HOVER_DELAY. Dismissal is click/Escape/swap.
                if (ENABLE_PERSISTENT_PREVIEW && currentPreview) {{
                    return;
                }}
                hidePreview();
            }});

            card.addEventListener('click', () => {{
                isPlaying = true;
                hideProgressIndicator(card);
                hidePreview();
                setTimeout(() => {{ isPlaying = false; }}, 2000);
            }});

            // Optional: keyboard / D-pad focus also triggers the preview, for
            // TV browsers and spatial-navigation users. The card itself is
            // usually not focusable — Jellyfin's focusable element is an
            // anchor inside (.cardImageContainer.itemAction) — but focusin
            // bubbles, so the card-level listener catches descendant focus.
            // Gate on event.target's :focus-visible (not the card's) so a
            // mouse click that focuses the inner anchor does not re-fire.
            if (ENABLE_FOCUS_TRIGGER) {{
                card.addEventListener('focusin', (e) => {{
                    if (isPlaying) return;
                    const t = e.target;
                    if (t && typeof t.matches === 'function' && !t.matches(':focus-visible')) return;
                    clearTimeout(hoverTimeout);
                    hoverTimeout = setTimeout(() => {{
                        showPreview(card, itemId);
                    }}, HOVER_DELAY);
                    showProgressIndicator(card);
                }});

                card.addEventListener('focusout', (e) => {{
                    // Ignore focus moves to another descendant of the same card.
                    if (e.relatedTarget && card.contains(e.relatedTarget)) return;
                    clearTimeout(hoverTimeout);
                    hideProgressIndicator(card);
                    if (ENABLE_PERSISTENT_PREVIEW && currentPreview) return;
                    hidePreview();
                }});
            }}
        }});

        if (newCardsCount > 0) {{
            console.log(`[HoverTrailer] Attached hover listeners to ${{newCardsCount}} new cards`);
        }}
    }}

    // Initialize when DOM is ready
    if (document.readyState === 'loading') {{
        document.addEventListener('DOMContentLoaded', attachHoverListeners);
    }} else {{
        attachHoverListeners();
    }}

    // Re-attach listeners when navigation occurs (debounced)
    const observer = new MutationObserver((mutations) => {{
        // Check if any mutations added item cards (Movie or Series)
        let hasItemCardChanges = false;
        for (const mutation of mutations) {{
            if (mutation.addedNodes.length > 0) {{
                for (const node of mutation.addedNodes) {{
                    if (node.nodeType === 1) {{ // Element node
                        // Check if it's an item card or contains item cards
                        if (node.matches && node.matches('.card[data-type=""Movie""]:not(.chapterCard), .card[data-type=""Series""]:not(.chapterCard), .card[data-itemtype=""Movie""]:not(.chapterCard), .card[data-itemtype=""Series""]:not(.chapterCard)')) {{
                            hasItemCardChanges = true;
                            break;
                        }}
                        if (node.querySelector && node.querySelector('.card[data-type=""Movie""]:not(.chapterCard), .card[data-type=""Series""]:not(.chapterCard), .card[data-itemtype=""Movie""]:not(.chapterCard), .card[data-itemtype=""Series""]:not(.chapterCard)')) {{
                            hasItemCardChanges = true;
                            break;
                        }}
                    }}
                }}
            }}
            if (hasItemCardChanges) break;
        }}

        // Only process if item cards were added
        if (hasItemCardChanges) {{
            // Debounce to prevent excessive re-attachment
            clearTimeout(mutationDebounce);
            mutationDebounce = setTimeout(() => {{
                log('DOM mutation detected, re-attaching listeners...');
                attachHoverListeners();
            }}, 500);
        }}
    }});

    observer.observe(document.body, {{
        childList: true,
        subtree: true
    }});

    log('HoverTrailer script initialized');
}})();
";
    }

    /// <summary>
    /// Determines the source of a remote trailer URL.
    /// </summary>
    /// <param name="url">The trailer URL to analyze.</param>
    /// <returns>A user-friendly source name.</returns>
    private static string GetTrailerSource(string? url)
    {
        if (string.IsNullOrEmpty(url))
            return "Unknown";

        try
        {
            var uri = new Uri(url);
            var host = uri.Host.ToLowerInvariant();

            return host switch
            {
                var h when h.Contains("youtube.com") || h.Contains("youtu.be") => "YouTube",
                var h when h.Contains("vimeo.com") => "Vimeo",
                var h when h.Contains("dailymotion.com") => "Dailymotion",
                var h when h.Contains("twitch.tv") => "Twitch",
                var h when h.Contains("facebook.com") => "Facebook",
                var h when h.Contains("instagram.com") => "Instagram",
                var h when h.Contains("tiktok.com") => "TikTok",
                _ => host
            };
        }
        catch (UriFormatException)
        {
            return "External";
        }
    }

    /// <summary>
    /// Generates a unique request ID for tracking purposes.
    /// </summary>
    /// <returns>A unique request identifier.</returns>
    private static string GenerateRequestId()
    {
        return $"REQ_{DateTime.UtcNow:yyyyMMddHHmmss}_{Guid.NewGuid():N}";
    }
}

/// <summary>
/// Trailer types supported by the plugin.
/// </summary>
public enum TrailerType
{
    /// <summary>
    /// Local trailer file stored on the server.
    /// </summary>
    Local,

    /// <summary>
    /// Remote trailer (e.g., YouTube) referenced by Jellyfin.
    /// </summary>
    Remote,

    /// <summary>
    /// Trailer downloaded via yt-dlp.
    /// </summary>
    Downloaded,

    /// <summary>
    /// Theme video used as trailer fallback.
    /// </summary>
    ThemeVideo
}

/// <summary>
/// Trailer information model.
/// </summary>
public class TrailerInfo
{
    /// <summary>
    /// Gets or sets the trailer ID.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Gets or sets the trailer name.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// Gets or sets the trailer path or URL.
    /// </summary>
    public string? Path { get; set; }

    /// <summary>
    /// Gets or sets the runtime in ticks.
    /// </summary>
    public long? RunTimeTicks { get; set; }

    /// <summary>
    /// Gets or sets the duration in seconds (derived from RunTimeTicks).
    /// </summary>
    public int? Duration => RunTimeTicks.HasValue ? (int?)(RunTimeTicks.Value / TimeSpan.TicksPerSecond) : null;

    /// <summary>
    /// Gets or sets a value indicating whether the trailer has subtitles.
    /// </summary>
    public bool HasSubtitles { get; set; }

    /// <summary>
    /// Gets or sets the trailer type.
    /// </summary>
    public TrailerType TrailerType { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether this is a remote trailer.
    /// </summary>
    public bool IsRemote { get; set; }

    /// <summary>
    /// Gets or sets the trailer source description.
    /// </summary>
    public string Source { get; set; } = "Unknown";
}

/// <summary>
/// Movie trailer information model.
/// </summary>
public class MovieTrailerInfo
{
    /// <summary>
    /// Gets or sets the movie ID.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Gets or sets the movie name.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the movie has a trailer.
    /// </summary>
    public bool HasTrailer { get; set; }

    /// <summary>
    /// Gets or sets the number of trailers.
    /// </summary>
    public int TrailerCount { get; set; }
}

/// <summary>
/// Hover trailer status model.
/// </summary>
public class HoverTrailerStatus
{
    /// <summary>
    /// Gets or sets a value indicating whether hover preview is enabled.
    /// </summary>
    public bool IsEnabled { get; set; }

    /// <summary>
    /// Gets or sets the hover delay in milliseconds.
    /// </summary>
    public int HoverDelayMs { get; set; }
}