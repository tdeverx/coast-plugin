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

            File.Delete(Path.Combine(directory, "no-crop"));
            // Cache isolation uses the authenticated credential and source, not just item ID.
            controller.Request.Headers.Authorization = "other-controlled-credential";
            var beforeCredential = encoder.Reads;
            Check(Body(await controller.DetectCrop(new(item))).Crop == expected
                && encoder.Reads == beforeCredential + 1, "Another credential must not reuse the first credential's crop.");
            controller.Request.Headers.Authorization = "controlled-test-only";

            // Hold distinct children: the production semaphore admits at most two.
            File.Delete(Path.Combine(directory, "release"));
            var startedBefore = Directory.GetFiles(directory, "started-*").Length;
            var parallel = Enumerable.Range(0, 8).Select(_ => controller.DetectCrop(new(Guid.NewGuid().ToString("N")))).ToArray();
            owned.AddRange(parallel);
            await WaitForAsync(() => Directory.GetFiles(directory, "started-*").Length == startedBefore + 2);
            await Task.Delay(100);
            Check(Directory.GetFiles(directory, "started-*").Length == startedBefore + 2,
                "Distinct crop requests must admit only two simultaneous children.");
            File.WriteAllText(Path.Combine(directory, "release"), "");
            Check((await Task.WhenAll(parallel)).All(result => Body(result).Error is null), "Queued crop requests must eventually complete.");

            // A departing waiter must not cancel a shared flight still in use.
            File.Delete(Path.Combine(directory, "release"));
            using var departing = new CancellationTokenSource();
            var firstController = Controller(encoder, logger);
            firstController.HttpContext.RequestAborted = departing.Token;
            var sharedItem = Guid.NewGuid().ToString("N");
            var initial = Directory.GetFiles(directory, "started-*").Length;
            var firstWaiter = firstController.DetectCrop(new(sharedItem));
            var remainingWaiter = controller.DetectCrop(new(sharedItem));
            owned.Add(remainingWaiter);
            await WaitForAsync(() => Directory.GetFiles(directory, "started-*").Length == initial + 1);
            departing.Cancel();
            try { await firstWaiter; throw new Exception("Departing request must cancel."); }
            catch (OperationCanceledException) { _checks++; }
            Check(!remainingWaiter.IsCompleted, "One cancelled consumer must not cancel a retained flight.");
            File.WriteAllText(Path.Combine(directory, "release"), "");
            Check(Body(await remainingWaiter).Error is null, "Remaining crop consumer must complete normally.");

            // Last-consumer departure kills/drains the child and releases its slot.
            File.Delete(Path.Combine(directory, "release"));
            using var last = new CancellationTokenSource();
            var lastController = Controller(encoder, logger);
            lastController.HttpContext.RequestAborted = last.Token;
            var beforeLast = Directory.GetFiles(directory, "started-*").ToHashSet();
            var lastWaiter = lastController.DetectCrop(new(Guid.NewGuid().ToString("N")));
            await WaitForAsync(() => Directory.GetFiles(directory, "started-*").Length > beforeLast.Count);
            var startedPath = Directory.GetFiles(directory, "started-*").Single(path => !beforeLast.Contains(path));
            var pid = int.Parse(Path.GetFileName(startedPath)["started-".Length..]);
            last.Cancel();
            try { await lastWaiter; throw new Exception("Last consumer must cancel."); }
            catch (OperationCanceledException) { _checks++; }
            try { Check(System.Diagnostics.Process.GetProcessById(pid).HasExited, "Cancelled child must exit before release."); }
            catch (ArgumentException) { _checks++; }
            File.WriteAllText(Path.Combine(directory, "release"), "");

            // Completed entries are evicted rather than growing for every unique video.
            for (var index = 0; index < 70; index++)
                await controller.DetectCrop(new(Guid.NewGuid().ToString("N")));
            var cache = (System.Collections.IDictionary)cacheValue;
            Check(cache.Count <= 64, "Running plus completed crop entries must remain bounded.");

            var expiryItem = Guid.NewGuid().ToString("N");
            await controller.DetectCrop(new(expiryItem));
            foreach (var flight in cache.Values)
                flight!.GetType().GetField("Used")!.SetValue(flight, DateTime.UtcNow.AddMinutes(-16));
            var beforeExpiry = encoder.Reads;
            await controller.DetectCrop(new(expiryItem));
            Check(cache.Count == 1 && encoder.Reads == beforeExpiry + 1, "Expired entries must be removed before reuse.");

            var missingEncoder = new Encoder { Path = "" };
            var retryController = Controller(missingEncoder, logger);
            var retryItem = Guid.NewGuid().ToString("N");
            var rejected = await retryController.DetectCrop(new(retryItem));
            Check(rejected.Result is ObjectResult { StatusCode: 502 } && rejected.Result is ObjectResult,
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
            Check(logger.Warnings == 1, "Only failed requests must be logged.");
            Console.WriteLine($"{_checks} crop checks passed (single flight, credential isolation, two-child admission, 64-entry cache, expiry, shared/last-consumer cancellation, retry, request contracts). Controlled child output is not actual media evidence.");
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
