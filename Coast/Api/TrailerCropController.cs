using System.Security.Cryptography;
using System.Text;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Coast.Api;

/// <summary>Detects encoded black bars in an authenticated Jellyfin video.</summary>
[Authorize]
[ApiController]
[Route("Coast")]
public sealed partial class TrailerCropController : ControllerBase
{
    // One bounded owner for running, queued and completed analyses. Entries are
    // credential/source scoped; only the final waiter may cancel shared work.
    private const int Capacity = 64;
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(15);
    private static readonly object CacheGate = new();
    private static readonly Dictionary<string, Flight> CropCache = new(StringComparer.Ordinal);
    private static readonly SemaphoreSlim Processes = new(2, 2);
    private sealed class Flight
    {
        public readonly CancellationTokenSource Cancellation = new();
        public readonly Lazy<Task<CropResult?>> Work;
        public int Waiters;
        public DateTime Used = DateTime.UtcNow;
        public Flight(Func<CancellationToken, Task<CropResult?>> analyze) =>
            Work = new(() => analyze(Cancellation.Token), LazyThreadSafetyMode.ExecutionAndPublication);
    }
    private readonly IMediaEncoder _mediaEncoder;
    private readonly ILogger<TrailerCropController> _logger;

    /// <summary>Initializes the crop controller.</summary>
    public TrailerCropController(IMediaEncoder mediaEncoder, ILogger<TrailerCropController> logger)
    {
        _mediaEncoder = mediaEncoder;
        _logger = logger;
    }

    /// <summary>Analyzes an authenticated Jellyfin video stream and returns its stable crop rectangle.</summary>
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
        Request.HttpContext.RequestAborted.ThrowIfCancellationRequested();
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{source}\n{authorization}")));
        Flight selected;
        lock (CacheGate)
        {
            foreach (var old in CropCache.Where(pair => pair.Value.Waiters == 0
                && pair.Value.Work.IsValueCreated && pair.Value.Work.Value.IsCompleted
                && DateTime.UtcNow - pair.Value.Used >= CacheLifetime).ToArray())
            {
                CropCache.Remove(old.Key);
                old.Value.Cancellation.Dispose();
            }
            if (!CropCache.TryGetValue(key, out selected!))
            {
                if (CropCache.Count >= Capacity)
                {
                    var oldest = CropCache.Where(pair => pair.Value.Waiters == 0
                        && pair.Value.Work.IsValueCreated && pair.Value.Work.Value.IsCompleted)
                        .OrderBy(pair => pair.Value.Used).FirstOrDefault();
                    if (oldest.Value is null)
                    {
                        Response.Headers.RetryAfter = "1";
                        return StatusCode(StatusCodes.Status503ServiceUnavailable, new CropResponse(null, "analysis_busy"));
                    }
                    CropCache.Remove(oldest.Key);
                    oldest.Value.Cancellation.Dispose();
                }
                selected = new Flight(token => AnalyzeAsync(itemId, authorization, source, token));
                CropCache.Add(key, selected);
            }
            selected.Waiters++;
            selected.Used = DateTime.UtcNow;
        }
        try
        {
            return Ok(new CropResponse(await selected.Work.Value.WaitAsync(Request.HttpContext.RequestAborted).ConfigureAwait(false), null));
        }
        catch (OperationCanceledException) when (Request.HttpContext.RequestAborted.IsCancellationRequested)
        {
            // A disconnected consumer is not an upstream analysis failure.
            throw;
        }
        catch (Exception exception)
        {
            lock (CacheGate)
                if (CropCache.TryGetValue(key, out var current) && ReferenceEquals(current, selected))
                    CropCache.Remove(key);
            _logger.LogWarning(exception, "Coast could not analyze video crop for item {ItemId}.", itemId);
            return StatusCode(StatusCodes.Status502BadGateway, new CropResponse(null, "analysis_failed"));
        }
        finally
        {
            var cancel = false;
            lock (CacheGate)
            {
                selected.Waiters--;
                selected.Used = DateTime.UtcNow;
                if (selected.Waiters == 0 && selected.Work.IsValueCreated && !selected.Work.Value.IsCompleted)
                {
                    if (CropCache.TryGetValue(key, out var current) && ReferenceEquals(current, selected))
                        CropCache.Remove(key);
                    cancel = true;
                }
            }
            if (cancel)
            {
                selected.Cancellation.Cancel();
                try { await selected.Work.Value.ConfigureAwait(false); } catch (Exception) { /* Drain the cancelled shared child. */ }
                selected.Cancellation.Dispose();
            }
            else
            {
                lock (CacheGate)
                    if (selected.Waiters == 0 && !CropCache.Values.Contains(selected))
                        selected.Cancellation.Dispose();
            }
        }
    }

    private async Task<CropResult?> AnalyzeAsync(string itemId, string authorization, string source, CancellationToken cancellation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        await Processes.WaitAsync(timeout.Token).ConfigureAwait(false);
        try
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
                    RedirectStandardOutput = false,
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

            var diagnosticsTask = ReadDiagnosticsAsync(process.StandardError, timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(true);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                try { await diagnosticsTask.ConfigureAwait(false); } catch (OperationCanceledException) { }
                cancellation.ThrowIfCancellationRequested();
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
        finally { Processes.Release(); }
    }

    private static async Task<string> ReadDiagnosticsAsync(StreamReader reader, CancellationToken cancellation)
    {
        // Keep only the tail (cropdetect's final rectangle); continue draining so
        // the child cannot deadlock on a full stderr pipe.
        var text = new StringBuilder();
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellation).ConfigureAwait(false)) != 0)
        {
            text.Append(buffer, 0, count);
            if (text.Length > 65536) text.Remove(0, text.Length - 65536);
        }
        return text.ToString();
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
