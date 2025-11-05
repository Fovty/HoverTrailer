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
    /// Gets trailer information for a specific movie.
    /// </summary>
    /// <param name="movieId">The movie ID.</param>
    /// <returns>The trailer information.</returns>
    [HttpGet("TrailerInfo/{movieId}")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public ActionResult<TrailerInfo> GetTrailerInfo([FromRoute] Guid movieId)
    {
        var requestId = GenerateRequestId();

        try
        {
            if (movieId == Guid.Empty)
            {
                LoggingHelper.LogWarning(_logger, "Invalid movie ID provided: {MovieId}", movieId);
                var invalidError = new ErrorResponse("INVALID_ARGUMENT", "Movie ID cannot be empty")
                {
                    RequestId = requestId
                };
                return BadRequest(invalidError);
            }

            var movie = _libraryManager.GetItemById(movieId) as Movie;
            if (movie == null)
            {
                LoggingHelper.LogDebug(_logger, "Movie not found with ID: {MovieId}", movieId);
                var notFoundError = new ErrorResponse("MOVIE_NOT_FOUND", "Movie not found", $"No movie found with ID: {movieId}")
                {
                    RequestId = requestId
                };
                return NotFound(notFoundError);
            }

            // Multi-source trailer detection with priority: Local → Remote → Downloaded
            LoggingHelper.LogDebug(_logger, "Starting multi-source trailer detection for movie: {MovieName} (ID: {MovieId})", movie.Name, movieId);
            LoggingHelper.LogDebug(_logger, "Movie path: {MoviePath}", movie.Path ?? "null");
            LoggingHelper.LogDebug(_logger, "Movie directory: {MovieDirectory}", movie.Path != null ? System.IO.Path.GetDirectoryName(movie.Path) ?? "null" : "null");

            TrailerInfo? trailerInfo = null;

            // Step 1: Check for local trailers using the same approach as Jellyfin's native implementation
            LoggingHelper.LogDebug(_logger, "Step 1: Checking for local trailers...");

            IEnumerable<BaseItem> localTrailers;
            if (movie is IHasTrailers hasTrailers)
            {
                // Use LocalTrailers property which matches Jellyfin's native trailer selection
                localTrailers = hasTrailers.LocalTrailers;
                LoggingHelper.LogDebug(_logger, "Using LocalTrailers property: Found {LocalTrailerCount} local trailers for movie: {MovieName}",
                    localTrailers.Count(), movie.Name);
            }
            else
            {
                // Fallback to GetExtras if movie doesn't implement IHasTrailers
                localTrailers = movie.GetExtras(new[] { ExtraType.Trailer });
                LoggingHelper.LogDebug(_logger, "Using GetExtras fallback: Found {LocalTrailerCount} local trailers for movie: {MovieName}",
                    localTrailers.Count(), movie.Name);
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
                LoggingHelper.LogDebug(_logger, "Found local trailer for movie: {MovieName} (ID: {MovieId})", movie.Name, movieId);
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

                LoggingHelper.LogDebug(_logger, "Successfully created local trailer info for movie: {MovieName} (ID: {MovieId})",
                    movie.Name, movieId);
                return Ok(trailerInfo);
            }

            // Step 2: Check for remote trailers if no local trailer found
            LoggingHelper.LogDebug(_logger, "Step 2: No local trailer found, checking for remote trailers...");

            if (movie.RemoteTrailers?.Any() == true)
            {
                var remoteTrailer = movie.RemoteTrailers.LastOrDefault();
                LoggingHelper.LogDebug(_logger, "Found remote trailer for movie: {MovieName} (ID: {MovieId})", movie.Name, movieId);

                trailerInfo = new TrailerInfo
                {
                    Id = movieId, // Use movie ID since remote trailers don't have their own ID
                    Name = remoteTrailer.Name ?? $"{movie.Name} - Trailer",
                    Path = remoteTrailer.Url,
                    RunTimeTicks = null, // Remote trailers typically don't have runtime info
                    HasSubtitles = false, // Remote trailers typically don't have subtitle info
                    TrailerType = TrailerType.Remote,
                    IsRemote = true,
                    Source = GetTrailerSource(remoteTrailer.Url)
                };

                LoggingHelper.LogDebug(_logger, "Successfully created remote trailer info for movie: {MovieName} (ID: {MovieId}), Source: {Source}",
                    movie.Name, movieId, trailerInfo.Source);
                return Ok(trailerInfo);
            }

            // Step 3: No trailers found (local or remote)
            LoggingHelper.LogDebug(_logger, "No local or remote trailers found for movie: {MovieName} (ID: {MovieId})", movie.Name, movieId);

            // Also check if there are any files in the movie directory that might be trailers (for debugging)
            var movieDir = System.IO.Path.GetDirectoryName(movie.Path);
            if (!string.IsNullOrEmpty(movieDir) && System.IO.Directory.Exists(movieDir))
            {
                var files = System.IO.Directory.GetFiles(movieDir, "*", System.IO.SearchOption.TopDirectoryOnly);
                LoggingHelper.LogDebug(_logger, "Files in movie directory {MovieDir}: {Files}",
                    movieDir, string.Join(", ", files.Select(System.IO.Path.GetFileName)));

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

            var error = new ErrorResponse("TRAILER_NOT_FOUND", "No trailer found for this movie",
                $"Movie '{movie.Name}' does not have any local or remote trailers available")
            {
                RequestId = requestId
            };
            return NotFound(error);
        }
        catch (UnauthorizedAccessException ex)
        {
            LoggingHelper.LogError(_logger, ex, "Unauthorized access getting trailer info for movie {MovieId}", movieId);
            var error = ErrorResponse.FromException(ex, requestId);
            return StatusCode(403, error);
        }
        catch (Exception ex)
        {
            LoggingHelper.LogError(_logger, ex, "Unexpected error getting trailer info for movie {MovieId}", movieId);
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
    const ENABLE_BACKGROUND_BLUR = {config.EnableBackgroundBlur.ToString().ToLower()};

    let hoverTimeout;
    let currentPreview;
    let currentCardElement;
    let isPlaying = false;
    let resizeHandler;
    let attachedCards = new Set(); // Track cards that already have listeners
    let mutationDebounce = null;

    function log(message, ...args) {{
        if (DEBUG_LOGGING) {{
            console.log('[HoverTrailer]', message, ...args);
        }}
    }}

    function applyBackgroundBlur() {{
        if (!ENABLE_BACKGROUND_BLUR) return;

        // Create or update backdrop blur element
        let backdrop = document.getElementById('hover-trailer-backdrop');
        if (!backdrop) {{
            backdrop = document.createElement('div');
            backdrop.id = 'hover-trailer-backdrop';
            backdrop.style.cssText = `
                position: fixed;
                top: 0;
                left: 0;
                right: 0;
                bottom: 0;
                backdrop-filter: blur(10px);
                -webkit-backdrop-filter: blur(10px);
                background: rgba(0, 0, 0, 0.1);
                z-index: 9999;
                pointer-events: none;
                opacity: 0;
                transition: opacity 0.3s ease;
            `;
            document.body.appendChild(backdrop);
        }}

        // Fade in the backdrop
        setTimeout(() => {{
            backdrop.style.opacity = '1';
        }}, 10);

        log('Background blur applied');
    }}

    function removeBackgroundBlur() {{
        if (!ENABLE_BACKGROUND_BLUR) return;

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
                overflow: hidden;
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
                overflow: hidden;
                box-shadow: 0 4px 12px rgba(0,0,0,0.5);
                z-index: 10000;
                pointer-events: none;
                opacity: 0;
                transition: opacity 0.3s ease;
            `;
        }}

        container.style.cssText = containerStyles;

        // Configure iframe for YouTube with proper attributes to prevent Error 153
        // Use object-fit: cover to maintain aspect ratio while filling the container
        iframe.style.cssText = `
            position: absolute;
            top: 50%;
            left: 50%;
            transform: translate(-50%, -50%);
            width: 100%;
            height: 100%;
            border: none;
            object-fit: cover;
        `;
        iframe.src = embedUrl;
        iframe.id = 'youtube-preview-' + Date.now(); // Unique ID for IFrame API
        // Critical attributes to prevent YouTube Error 153
        iframe.allow = 'accelerometer; autoplay; clipboard-write; encrypted-media; gyroscope; picture-in-picture';
        iframe.setAttribute('allowfullscreen', '');
        iframe.setAttribute('referrerpolicy', 'strict-origin-when-cross-origin');
        iframe.setAttribute('frameborder', '0');

        container.appendChild(iframe);
        log('Created YouTube preview iframe with URL:', embedUrl);

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
                    
                    if (volumePercent === 0) {{
                        // Keep muted if volume is 0 or audio is disabled
                        log('YouTube iframe kept muted (volume=0 or audio disabled)');
                    }} else {{
                        // Unmute and set volume immediately
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
                overflow: hidden;
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
                overflow: hidden;
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

    function showPreview(element, movieId) {{
        if (currentPreview || isPlaying) return;

        log('Showing preview for movie:', movieId);

        // Get trailer info from API
        fetch(`${{API_BASE_URL}}/HoverTrailer/TrailerInfo/${{movieId}}`)
            .then(response => {{
                if (!response.ok) {{
                    throw new Error('Trailer not found');
                }}
                return response.json();
            }})
            .then(trailerInfo => {{
                // Check if preview was cancelled during fetch (another hover started)
                if (currentPreview || isPlaying) {{
                    log('Preview cancelled during fetch - another preview is active');
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
                        
                        videoSource = `https://www.youtube-nocookie.com/embed/${{videoId}}?` +
                            `autoplay=1` +
                            `&mute=1` +                  // Always start muted to prevent loud audio spike
                            `&controls=0` +
                            `&loop=1` +
                            `&playlist=${{videoId}}` +  // Required for loop to work
                            `&playsinline=1` +           // Mobile compatibility
                            `&rel=0` +                   // No related videos
                            `&modestbranding=1` +        // Minimal branding
                            `&enablejsapi=1`;            // Enable JS API for volume and quality control
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
                            const scaledWidth = Math.round(newWidth * (PREVIEW_SIZE_PERCENTAGE / 100));
                            const scaledHeight = Math.round(newHeight * (PREVIEW_SIZE_PERCENTAGE / 100));

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

                // Add resize handler to reposition container on window resize
                resizeHandler = () => {{
                    if (currentPreview && currentCardElement) {{
                        const cardRect = currentCardElement.getBoundingClientRect();
                        const cardCenterX = cardRect.left + cardRect.width / 2;
                        const cardCenterY = cardRect.top + cardRect.height / 2;

                        // Recalculate container size if in FitContent mode
                        if (PREVIEW_SIZING_MODE === 'FitContent') {{
                            const video = currentPreview.querySelector('video');
                            if (video && video.videoWidth && video.videoHeight) {{
                                const videoAspectRatio = video.videoWidth / video.videoHeight;
                                const cardAspectRatio = cardRect.width / cardRect.height;

                                let newWidth, newHeight;
                                if (videoAspectRatio > cardAspectRatio) {{
                                    newWidth = cardRect.width;
                                    newHeight = Math.round(cardRect.width / videoAspectRatio);
                                }} else {{
                                    newHeight = cardRect.height;
                                    newWidth = Math.round(cardRect.height * videoAspectRatio);
                                }}

                                // Apply percentage scaling
                                const scaledWidth = Math.round(newWidth * (PREVIEW_SIZE_PERCENTAGE / 100));
                                const scaledHeight = Math.round(newHeight * (PREVIEW_SIZE_PERCENTAGE / 100));

                                currentPreview.style.width = `${{scaledWidth}}px`;
                                currentPreview.style.height = `${{scaledHeight}}px`;
                            }}
                        }}

                        currentPreview.style.top = `calc(${{cardCenterY}}px + ${{PREVIEW_OFFSET_Y}}px)`;
                        currentPreview.style.left = `calc(${{cardCenterX}}px + ${{PREVIEW_OFFSET_X}}px)`;
                    }}
                }};
                window.addEventListener('resize', resizeHandler);

                log('Preview created for:', trailerInfo.Name);
            }})
            .catch(error => {{
                log('Error loading trailer:', error);
            }});
    }}

    function hidePreview() {{
        if (currentPreview) {{
            log('Hiding preview');
            const previewToRemove = currentPreview;
            const videoToStop = previewToRemove.querySelector('video');
            const iframeToStop = previewToRemove.querySelector('iframe');

            // Stop video or iframe immediately
            if (videoToStop) {{
                log('Stopping video element');
                videoToStop.pause();
                videoToStop.src = '';
                videoToStop.load();
            }}
            
            if (iframeToStop) {{
                log('Stopping iframe (YouTube)');
                iframeToStop.src = 'about:blank';
            }}

            // Fade out animation
            currentPreview.style.opacity = '0';
            removeBackgroundBlur();

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
        const movieCards = document.querySelectorAll('[data-type=""Movie""], .card[data-itemtype=""Movie""]');

        let newCardsCount = 0;
        movieCards.forEach(card => {{
            const movieId = card.getAttribute('data-id') || card.getAttribute('data-itemid');
            if (!movieId) return;

            // Skip if this card already has listeners attached
            if (attachedCards.has(movieId)) return;

            attachedCards.add(movieId);
            newCardsCount++;

            card.addEventListener('mouseenter', (e) => {{
                if (isPlaying) return;

                clearTimeout(hoverTimeout);
                hoverTimeout = setTimeout(() => {{
                    showPreview(card, movieId);
                }}, HOVER_DELAY);
            }});

            card.addEventListener('mouseleave', () => {{
                clearTimeout(hoverTimeout);
                hidePreview();
            }});

            card.addEventListener('click', () => {{
                isPlaying = true;
                hidePreview();
                setTimeout(() => {{ isPlaying = false; }}, 2000);
            }});
        }});

        if (newCardsCount > 0) {{
            log('Attached hover listeners to', newCardsCount, 'new movie cards');
        }}
    }}

    // Initialize when DOM is ready
    if (document.readyState === 'loading') {{
        document.addEventListener('DOMContentLoaded', attachHoverListeners);
    }} else {{
        attachHoverListeners();
    }}

    // Re-attach listeners when navigation occurs (debounced)
    const observer = new MutationObserver(() => {{
        // Debounce to prevent excessive re-attachment
        clearTimeout(mutationDebounce);
        mutationDebounce = setTimeout(() => {{
            attachHoverListeners();
        }}, 500); // Wait 500ms after last DOM change
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
    Downloaded
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