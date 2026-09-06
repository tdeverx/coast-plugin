using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Coast.Api;

/// <summary>Detects encoded black bars in a current user's Jellyfin trailer.</summary>
[Authorize]
[ApiController]
[Route("Coast")]
public sealed partial class TrailerCropController : ControllerBase
{
    private static readonly ConcurrentDictionary<string, Task<CropResult?>> CropCache = new(StringComparer.Ordinal);
    private readonly IMediaEncoder _mediaEncoder;
    private readonly ILogger<TrailerCropController> _logger;

    /// <summary>Initializes the crop controller.</summary>
    public TrailerCropController(IMediaEncoder mediaEncoder, ILogger<TrailerCropController> logger)
    {
        _mediaEncoder = mediaEncoder;
        _logger = logger;
    }

    /// <summary>Analyzes a Jellyfin trailer stream and returns its stable crop rectangle.</summary>
    [HttpPost("TrailerCrop")]
    public async Task<ActionResult<CropResponse>> DetectCrop([FromBody] CropRequest request)
    {
        if (Plugin.Instance?.Configuration.EnableTrailerCrop != true)
        {
            return NotFound();
        }

        var itemId = request.ItemId?.Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
        var authorization = Request.Headers.Authorization.ToString();
        if (itemId is null || !ItemIdRegex().IsMatch(itemId)
            || string.IsNullOrWhiteSpace(authorization)
            || authorization.Contains('\r')
            || authorization.Contains('\n'))
        {
            return BadRequest(new CropResponse(null, "invalid_request"));
        }

        var source = $"{Request.Scheme}://{Request.Host}{Request.PathBase}/Videos/{itemId}/stream?Static=true";
        var task = CropCache.GetOrAdd(itemId, _ => AnalyzeAsync(itemId, authorization, source));
        try
        {
            return Ok(new CropResponse(await task.ConfigureAwait(false), null));
        }
        catch (Exception exception)
        {
            CropCache.TryRemove(itemId, out _);
            _logger.LogWarning(exception, "Coast could not analyze trailer crop for item {ItemId}.", itemId);
            return StatusCode(StatusCodes.Status502BadGateway, new CropResponse(null, "analysis_failed"));
        }
    }

    private async Task<CropResult?> AnalyzeAsync(string itemId, string authorization, string source)
    {
        var configuredPath = Plugin.Instance?.Configuration.FfmpegPath;
        var ffmpegPath = string.IsNullOrWhiteSpace(configuredPath) ? _mediaEncoder.EncoderPath : configuredPath;
        if (string.IsNullOrWhiteSpace(ffmpegPath))
        {
            throw new InvalidOperationException("Jellyfin has no configured FFmpeg executable.");
        }

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        string[] arguments =
        [
            "-hide_banner", "-loglevel", "info",
            "-headers", $"Authorization: {authorization}\r\n",
            "-ss", "2", "-t", "8", "-i", source,
            "-vf", "fps=2,cropdetect=limit=24/255:round=2:reset=0",
            "-an", "-threads", "1", "-f", "null", "-"
        ];
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        if (!process.Start())
        {
            throw new InvalidOperationException("FFmpeg did not start.");
        }

        var diagnosticsTask = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            process.Kill(true);
            throw new TimeoutException("FFmpeg crop analysis timed out.");
        }

        var diagnostics = await diagnosticsTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                string.Create(CultureInfo.InvariantCulture, $"FFmpeg exited with code {process.ExitCode}."));
        }

        var matches = CropRegex().Matches(diagnostics);
        if (matches.Count == 0)
        {
            return null;
        }

        var match = matches[^1];
        return new CropResult(
            int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
            int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture),
            int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture),
            int.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture));
    }

    [GeneratedRegex("^[0-9a-f]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex ItemIdRegex();

    [GeneratedRegex("crop=(\\d+):(\\d+):(\\d+):(\\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex CropRegex();

    /// <summary>Crop request body.</summary>
    public sealed record CropRequest(string? ItemId);

    /// <summary>Crop API response.</summary>
    public sealed record CropResponse(CropResult? Crop, string? Error);

    /// <summary>Detected crop rectangle.</summary>
    public sealed record CropResult(int Width, int Height, int X, int Y);
}
