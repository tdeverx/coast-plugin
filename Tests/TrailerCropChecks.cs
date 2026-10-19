using System.Collections.Concurrent;
using System.Reflection;
using Jellyfin.Plugin.Coast;
using Jellyfin.Plugin.Coast.Api;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

internal static class TrailerCropChecks
{
    private const string ChildDirectory = "COAST_CROP_TEST_CHILD_DIRECTORY";
    private static int _checks;

    // The harmless child stands in for FFmpeg. It never opens a stream or contacts Jellyfin.
    public static async Task<bool> RunChildAsync()
    {
        var directory = Environment.GetEnvironmentVariable(ChildDirectory);
        if (directory is null) return false;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory, $"started-{Environment.ProcessId}"), "");
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!File.Exists(Path.Combine(directory, "release")))
                await Task.Delay(10, deadline.Token);
            if (!File.Exists(Path.Combine(directory, "no-crop")))
                await Console.Error.WriteLineAsync("crop=1920:1000:0:40\ncrop=1880:980:20:50");
            return true;
        }
        finally
        {
            await File.WriteAllTextAsync(Path.Combine(directory, $"done-{Environment.ProcessId}"), "");
        }
    }

    public static async Task RunAsync()
    {
        var cacheValue = typeof(TrailerCropController)
            .GetField("CropCache", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var directory = Path.Combine(Path.GetTempPath(), $"coast-crop-check-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var previous = Environment.GetEnvironmentVariable(ChildDirectory);
        using var entered = new ManualResetEventSlim();
        using var releaseEncoder = new ManualResetEventSlim();
        var owned = new List<Task>();
        try
        {
            Plugin.Instance = new Plugin();
            Environment.SetEnvironmentVariable(ChildDirectory, directory);
            var encoder = new Encoder();
            var logger = new TestLogger();
            var controller = Controller(encoder, logger);
            var item = Guid.NewGuid().ToString("N");

            // Hold process creation itself while independent requests contend for the same entry.
            encoder.BeforeRead = () => { entered.Set(); releaseEncoder.Wait(TimeSpan.FromSeconds(5)); };
            var leader = Task.Run(() => controller.DetectCrop(new(item)));
            owned.Add(leader);
            Check(entered.Wait(TimeSpan.FromSeconds(5)), "Winner must reach process admission.");
            var followerThreads = new Thread?[7];
            var followers = Enumerable.Range(0, 7)
                .Select(index => Task.Factory.StartNew(() =>
                {
                    followerThreads[index] = Thread.CurrentThread;
                    return controller.DetectCrop(new(item));
                }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap()).ToArray();
            owned.AddRange(followers);
            await WaitForAsync(() => followerThreads.All(thread => thread is not null
                && (thread.ThreadState & ThreadState.WaitSleepJoin) != 0));
            var admitted = encoder.Reads;
            releaseEncoder.Set();
            await WaitForAsync(() => Directory.GetFiles(directory, "started-*").Length > 0);
            File.WriteAllText(Path.Combine(directory, "release"), "");
            var responses = await Task.WhenAll(followers.Prepend(leader));
            await WaitForAsync(() => Directory.GetFiles(directory, "done-*").Length == encoder.Reads);
            Console.WriteLine($"Controlled crop contention: 8 requests, {admitted} analysis admissions.");
            Check(admitted == 1, "Only the winning Lazy may enter analysis while all followers wait.");
            Check(encoder.Reads == 1, "Contending requests must evaluate only the selected analysis.");
            var expected = new TrailerCropController.CropResult(1880, 980, 20, 50);
            Check(responses.All(result => Body(result) == new TrailerCropController.CropResponse(expected, null)),
                "All waiters must share the final cropdetect match.");
            Check(Directory.GetFiles(directory, "started-*").Length == 1,
                "Same-key requests must create exactly one controlled child.");

            encoder.BeforeRead = null;
            var cached = await controller.DetectCrop(new(Guid.ParseExact(item, "N").ToString("D").ToUpperInvariant()));
            Check(Body(cached).Crop == expected && encoder.Reads == 1,
                "Normalized IDs must retain successful analysis.");

            File.WriteAllText(Path.Combine(directory, "no-crop"), "");
            var emptyItem = Guid.NewGuid().ToString("N");
            var empty = await controller.DetectCrop(new(emptyItem));
            var emptyCached = await controller.DetectCrop(new(emptyItem));
            Check(Body(empty) == new TrailerCropController.CropResponse(null, null)
                && Body(emptyCached) == Body(empty) && encoder.Reads == 2,
                "A completed analysis without a crop must remain cached.");

            // Controlled task settlement schedules both old catches around a fresh replacement.
            // This exercises the actual catch block without adding a production test hook.
            var failedItem = Guid.NewGuid().ToString("N");
            var failed = new TaskCompletionSource<TrailerCropController.CropResult?>();
            var replacementResult = new TaskCompletionSource<TrailerCropController.CropResult?>();
            var old = new Lazy<Task<TrailerCropController.CropResult?>>(() => failed.Task);
            var replacement = new Lazy<Task<TrailerCropController.CropResult?>>(() => replacementResult.Task);
            var cache = (ConcurrentDictionary<string, Lazy<Task<TrailerCropController.CropResult?>>>)cacheValue;
            Check(cache.TryAdd(failedItem, old), "Controlled failed flight must be admitted.");
            var first = controller.DetectCrop(new(failedItem));
            var second = controller.DetectCrop(new(failedItem));
            Task<ActionResult<TrailerCropController.CropResponse>>? retried = null;
            logger.OnWarning = () =>
            {
                if (Interlocked.Increment(ref logger.Failures) != 1) return;
                Check(cache.TryAdd(failedItem, replacement), "First failed waiter must evict its own flight.");
                retried = controller.DetectCrop(new(failedItem));
            };
            failed.SetException(new InvalidOperationException("Controlled analysis failure."));
            var failures = await Task.WhenAll(first, second);
            Check(failures.All(result => result.Result is ObjectResult { StatusCode: 502 }
                && Body(result).Error == "analysis_failed") && logger.Failures == 2,
                "Every failed waiter must retain the logged 502 contract.");
            Check(cache.TryGetValue(failedItem, out var current) && ReferenceEquals(current, replacement),
                "A late failed waiter must not evict a replacement flight.");
            replacementResult.SetResult(expected);
            Check(retried is not null && Body(await retried).Crop == expected,
                "A replacement admitted during failure must settle successfully.");
            Check(Body(await controller.DetectCrop(new(failedItem))).Crop == expected,
                "A successful retry must remain cached.");

            logger.OnWarning = null;
            var missingEncoder = new Encoder { Path = "" };
            var retryController = Controller(missingEncoder, logger);
            var retryItem = Guid.NewGuid().ToString("N");
            var rejected = await retryController.DetectCrop(new(retryItem));
            Check(rejected.Result is ObjectResult { StatusCode: 502 } && !cache.ContainsKey(retryItem),
                "An early analysis failure must be evicted.");
            missingEncoder.Path = Environment.ProcessPath!;
            Check(Body(await retryController.DetectCrop(new(retryItem))).Error is null,
                "An early failure must allow a later analysis attempt.");

            var beforeInvalid = encoder.Reads;
            Check((await controller.DetectCrop(new("not-an-id"))).Result is BadRequestObjectResult,
                "Invalid IDs must remain rejected.");
            controller.Request.Headers.Authorization = "bad\nheader";
            Check((await controller.DetectCrop(new(Guid.NewGuid().ToString("N")))).Result is BadRequestObjectResult,
                "Header injection must remain rejected.");
            controller.Request.Headers.Authorization = "";
            Check((await controller.DetectCrop(new(Guid.NewGuid().ToString("N")))).Result is BadRequestObjectResult,
                "Missing authorization must remain rejected.");
            Plugin.Instance.Configuration.EnableTrailerCrop = false;
            Check((await controller.DetectCrop(new(item))).Result is NotFoundResult && encoder.Reads == beforeInvalid,
                "Disabled crop must reject even cached results before process admission.");
            Check(logger.Warnings == 3, "Only failed requests must be logged.");
            Console.WriteLine($"{_checks} crop checks passed (single flight, normalized/null cache, identity eviction, retry, request contracts). Controlled child output is not actual media evidence.");
        }
        finally
        {
            releaseEncoder.Set();
            File.WriteAllText(Path.Combine(directory, "release"), "");
            try { await Task.WhenAll(owned).WaitAsync(TimeSpan.FromSeconds(30)); }
            catch { /* Preserve the original assertion; production analysis has its own 20s bound. */ }
            cacheValue.GetType().GetMethod("Clear")!.Invoke(cacheValue, null);
            Environment.SetEnvironmentVariable(ChildDirectory, previous);
            Directory.Delete(directory, true);
        }
    }

    private static TrailerCropController Controller(Encoder encoder, TestLogger logger)
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("crop-harness.invalid");
        context.Request.PathBase = "/jellyfin";
        context.Request.Headers.Authorization = "controlled-test-only";
        return new TrailerCropController(encoder, logger)
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };
    }

    private static TrailerCropController.CropResponse Body(ActionResult<TrailerCropController.CropResponse> result)
        => (TrailerCropController.CropResponse)((ObjectResult)result.Result!).Value!;

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        _checks++;
    }

    private static async Task WaitForAsync(Func<bool> predicate)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate()) await Task.Delay(10, deadline.Token);
    }

    private sealed class Encoder : IMediaEncoder
    {
        public int Reads;
        public string Path { get; set; } = Environment.ProcessPath!;
        public Action? BeforeRead { get; set; }
        public string EncoderPath
        {
            get
            {
                Interlocked.Increment(ref Reads);
                BeforeRead?.Invoke();
                return Path;
            }
        }
    }

    private sealed class TestLogger : ILogger<TrailerCropController>
    {
        public int Warnings;
        public int Failures;
        public Action? OnWarning { get; set; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel != LogLevel.Warning) return;
            Interlocked.Increment(ref Warnings);
            OnWarning?.Invoke();
        }
    }
}
