using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SW.Serverless.Sdk.Resident;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SW.Serverless.Samples.LargeFiles
{
    /// <summary>
    /// A resident adapter for LARGE FILES, and the case that makes the visibility story concrete.
    ///
    /// It streams a file chunk by chunk and pushes each chunk to the host, so the process's
    /// resident memory stays proportional to the chunk size rather than to the file size. Watch
    /// the Adapters page while a 1 GB file goes through: percent climbs, MB/s settles, and
    /// **memory does not move**. That flat line is the thing worth seeing — a naive
    /// ReadAllBytes adapter would show a 1 GB spike and, on a node running fifteen adapters,
    /// take the whole node with it.
    ///
    /// Everything here is constructor-injected: options bound from startup values, a reader
    /// service, a throughput meter, and an ILogger that lands in the host's own logs.
    /// </summary>
    public class LargeFileHandler : IResidentAdapter
    {
        readonly StreamOptions options;
        readonly IChunkReader reader;
        readonly ThroughputMeter meter;
        readonly ILogger<LargeFileHandler> logger;

        IAdapterContext context;
        CancellationTokenSource stopping;
        Task loop;

        readonly ManualResetEventSlim resumed = new(true);

        string currentFile;
        long currentBytes, currentTotal;
        int chunksSent, chunksAcked, chunksRejected;
        long filesCompleted, filesFailed;
        DateTimeOffset? lastMessageOn;
        string lastError;
        volatile string state = "Starting";

        public LargeFileHandler(IOptions<StreamOptions> options, IChunkReader reader,
            ThroughputMeter meter, ILogger<LargeFileHandler> logger)
        {
            this.options = options.Value;
            this.reader = reader;
            this.meter = meter;
            this.logger = logger;
        }

        // ------------------------------------------------------------------ lifecycle

        public Task StartAsync(IAdapterContext context, CancellationToken cancellationToken)
        {
            this.context = context;

            if (string.IsNullOrWhiteSpace(options.Path))
                throw new InvalidOperationException("Startup value 'Path' is required.");

            Directory.CreateDirectory(options.Path);
            Directory.CreateDirectory(ArchivePath);

            state = "Idle";
            logger.LogInformation("Watching {Path} for {Pattern}, {ChunkSizeKb} KB chunks.",
                options.Path, options.Pattern, options.ChunkSizeKb);

            stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            loop = Task.Run(() => ScanAsync(stopping.Token));
            return Task.CompletedTask;
        }

        string ArchivePath => options.ArchivePath ?? Path.Combine(options.Path, ".done");

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            state = "Draining";
            resumed.Set();
            stopping?.Cancel();
            if (loop != null) await Task.WhenAny(loop, Task.Delay(5000, cancellationToken));
            state = "Stopped";
        }

        public Task<AdapterStatus> GetStatusAsync()
        {
            var pending = 0;
            try { pending = Directory.GetFiles(options.Path, options.Pattern).Length; } catch { }

            var status = new AdapterStatus
            {
                Connected = Directory.Exists(options.Path),
                State = state,
                LastMessageOn = lastMessageOn,
                LastError = lastError,
                InFlight = chunksSent - chunksAcked - chunksRejected
            };

            // Everything a progress bar needs, on the ordinary heartbeat — no bespoke channel.
            status.Details["pendingFiles"] = pending.ToString();
            status.Details["filesCompleted"] = filesCompleted.ToString();
            status.Details["filesFailed"] = filesFailed.ToString();
            status.Details["chunkSizeKb"] = options.ChunkSizeKb.ToString();

            if (currentFile != null)
            {
                status.Details["currentFile"] = currentFile;
                status.Details["bytesRead"] = currentBytes.ToString();
                status.Details["totalBytes"] = currentTotal.ToString();
                status.Details["percent"] = currentTotal > 0
                    ? ((int)(currentBytes * 100 / currentTotal)).ToString()
                    : "0";
                status.Details["throughputMbPerSec"] = meter.MegabytesPerSecond.ToString("0.00");

                var remaining = meter.Remaining(currentTotal);
                if (remaining.HasValue)
                    status.Details["etaSeconds"] = ((int)remaining.Value.TotalSeconds).ToString();
            }

            status.Details["chunksSent"] = chunksSent.ToString();
            status.Details["chunksAcked"] = chunksAcked.ToString();
            status.Details["chunksRejected"] = chunksRejected.ToString();

            // Host-observed RSS is the honest number, but reporting our own makes the flat line
            // visible next to the file size right here.
            status.Details["selfWorkingSetMb"] =
                (Environment.WorkingSet / 1024 / 1024).ToString();

            return Task.FromResult(status);
        }

        // ------------------------------------------------------------------ the stream

        async Task ScanAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    foreach (var file in Directory.EnumerateFiles(options.Path, options.Pattern)
                                                  .OrderBy(f => f))
                    {
                        if (ct.IsCancellationRequested) return;
                        await StreamFileAsync(file, ct);
                    }
                    if (currentFile == null) state = "Idle";
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    logger.LogError(ex, "Scan failed.");
                }

                try { await Task.Delay(TimeSpan.FromSeconds(options.PollSeconds), ct); }
                catch (OperationCanceledException) { return; }
            }
        }

        async Task StreamFileAsync(string path, CancellationToken ct)
        {
            var name = Path.GetFileName(path);

            // An exclusive open is the only handoff that actually proves the producer is finished:
            // a writer holding the file with FileShare.Read would sail through a read-only probe and
            // we would stream — and then archive — a partial file. Producers that cannot cooperate
            // should write under a non-matching temporary name and rename it when complete; the
            // rename is atomic, so the scan never sees a half-written file under the watched name.
            try { using var _ = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None); }
            catch (IOException) { return; }     // still being written; next poll

            currentFile = name;
            currentTotal = new FileInfo(path).Length;
            currentBytes = 0;
            state = "Streaming";
            meter.Start();

            var accepted = true;
            var completed = false;      // only a clean run through every chunk sets this

            try
            {
                await foreach (var chunk in reader.ReadAsync(path, ct))
                {
                    resumed.Wait(ct);

                    var headers = new Dictionary<string, string>
                    {
                        ["file.name"] = name,
                        ["file.totalBytes"] = currentTotal.ToString(),
                        ["chunk.index"] = chunk.Index.ToString(),
                        ["chunk.count"] = chunk.Count.ToString(),
                        ["chunk.offset"] = chunk.Offset.ToString()
                    };

                    // PublishAsync blocks on the credit window, so the host controls how far
                    // ahead this loop may run. That is the backpressure: without it, a fast disk
                    // and a slow host would grow the queue until the process died.
                    var result = await context.PublishAsync(
                        chunk.Data,
                        dedupeKey: $"filestream:{name}:{currentTotal}:{chunk.Index}",
                        endpoint: name,
                        headers: headers,
                        contentType: "application/octet-stream",
                        cancellationToken: ct);

                    chunksSent++;

                    if (result.Accepted)
                    {
                        chunksAcked++;
                        currentBytes += chunk.Data.Length;
                        meter.Add(chunk.Data.Length);
                        lastMessageOn = DateTimeOffset.UtcNow;

                        if (chunk.Index % 20 == 0)
                        {
                            context.Metric("filestream.bytes", chunk.Data.Length * 20);
                            logger.LogDebug("Chunk {Index}/{Count} of {File} at {Rate} MB/s.",
                                chunk.Index, chunk.Count, name, meter.MegabytesPerSecond);
                        }
                    }
                    else
                    {
                        // A partial file is worse than no file, so stop and leave it in place to
                        // be retried from the beginning on the next poll.
                        chunksRejected++;
                        accepted = false;
                        lastError = result.Error;
                        logger.LogWarning("Host rejected chunk {Index} of {File}: {Error}. " +
                            "Abandoning this file; it stays put and will be retried whole.",
                            chunk.Index, name, result.Error);
                        break;
                    }

                    if (options.ThrottleMsPerChunk > 0)
                        await Task.Delay(options.ThrottleMsPerChunk, ct);
                }

                completed = accepted;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                accepted = false;
                filesFailed++;
                lastError = ex.Message;
                logger.LogError(ex, "Streaming {File} failed.", name);
            }
            finally
            {
                // Cancellation mid-stream leaves `accepted` true but the file half-sent. Archiving
                // on that would silently drop the remainder, so archive only on a completed run.
                if (completed)
                {
                    filesCompleted++;
                    logger.LogInformation(
                        "Finished {File}: {Bytes} bytes in {Chunks} chunks, {Seconds:0.0}s at {Rate} MB/s.",
                        name, currentTotal, chunksAcked, meter.Elapsed.TotalSeconds,
                        meter.MegabytesPerSecond);

                    try { File.Move(path, Path.Combine(ArchivePath, name), overwrite: true); }
                    catch (Exception ex) { logger.LogWarning(ex, "Could not archive {File}.", name); }
                }

                currentFile = null;
                currentTotal = currentBytes = 0;
                state = "Idle";
            }
        }

        // ------------------------------------------------------------------ commands

        /// <summary>
        /// Writes a file of the requested size so the streaming behaviour can be demonstrated
        /// without finding a large file first. It is written in chunks too — generating a
        /// gigabyte in memory would rather undermine the point.
        /// </summary>
        public async Task<object> GenerateTestFile(int megabytes)
        {
            if (megabytes is < 1 or > 4096)
                throw new ArgumentOutOfRangeException(nameof(megabytes), "Expected 1..4096 MB.");

            var name = $"generated-{megabytes}mb-{DateTime.UtcNow:HHmmss}.bin";
            var path = Path.Combine(options.Path, name + ".tmp");

            var block = new byte[1024 * 1024];
            Random.Shared.NextBytes(block);

            await using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write,
                             FileShare.None, 1024 * 1024, FileOptions.SequentialScan))
                for (var i = 0; i < megabytes; i++)
                    await stream.WriteAsync(block);

            // Rename only when complete, so the scan never picks up a half-written file.
            var final = Path.Combine(options.Path, name);
            File.Move(path, final, overwrite: true);

            logger.LogInformation("Generated {File} ({Megabytes} MB).", name, megabytes);
            return new { file = name, megabytes, note = "Watch percent climb while memory stays flat." };
        }

        /// <summary>Hold the stream mid-file, so a half-finished transfer can be inspected.</summary>
        public Task<object> Pause()
        {
            resumed.Reset();
            state = "Paused";
            logger.LogWarning("Streaming paused at {Bytes} bytes of {File}.", currentBytes, currentFile);
            return Task.FromResult<object>(new { paused = true, atBytes = currentBytes, file = currentFile });
        }

        public Task<object> Resume()
        {
            resumed.Set();
            state = currentFile == null ? "Idle" : "Streaming";
            return Task.FromResult<object>(new { paused = false });
        }

        public Task<object> SetChunkSize(int kilobytes)
        {
            if (kilobytes is < 4 or > 8192)
                throw new ArgumentOutOfRangeException(nameof(kilobytes), "Expected 4..8192 KB.");

            options.ChunkSizeKb = kilobytes;
            logger.LogInformation("Chunk size changed to {Kilobytes} KB; it applies to the next file.",
                kilobytes);
            return Task.FromResult<object>(new { chunkSizeKb = kilobytes });
        }

        public Task<object> GetProgress() => Task.FromResult<object>(new
        {
            state,
            currentFile,
            currentBytes,
            currentTotal,
            percent = currentTotal > 0 ? (int)(currentBytes * 100 / currentTotal) : 0,
            throughputMbPerSec = meter.MegabytesPerSecond,
            etaSeconds = (int?)meter.Remaining(currentTotal)?.TotalSeconds,
            chunksSent,
            chunksAcked,
            chunksRejected,
            filesCompleted,
            selfWorkingSetMb = Environment.WorkingSet / 1024 / 1024
        });
    }
}
