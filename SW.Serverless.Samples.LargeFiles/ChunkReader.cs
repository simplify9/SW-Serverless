using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace SW.Serverless.Samples.LargeFiles
{
    public readonly record struct Chunk(int Index, int Count, long Offset, ReadOnlyMemory<byte> Data);

    public interface IChunkReader
    {
        IAsyncEnumerable<Chunk> ReadAsync(string path, CancellationToken cancellationToken);
    }

    /// <summary>
    /// An ordinary injected service, with an injected logger and injected options — which is the
    /// point of the sample as much as the streaming is.
    ///
    /// It reads into ONE rented buffer and yields slices of it. Nothing here ever holds the whole
    /// file, so a 2 GB file costs the same resident memory as a 2 MB one.
    /// </summary>
    public class ChunkReader : IChunkReader
    {
        readonly StreamOptions options;
        readonly ILogger<ChunkReader> logger;

        public ChunkReader(IOptions<StreamOptions> options, ILogger<ChunkReader> logger)
        {
            this.options = options.Value;
            this.logger = logger;
        }

        public async IAsyncEnumerable<Chunk> ReadAsync(string path,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var size = Math.Max(4, options.ChunkSizeKb) * 1024;
            var length = new FileInfo(path).Length;
            var count = (int)Math.Max(1, (length + size - 1) / size);

            logger.LogInformation("Streaming {File} — {Bytes} bytes in {Count} chunks of {Size} KB.",
                Path.GetFileName(path), length, count, size / 1024);

            // Sequential scan, no buffering by the OS beyond what we ask for.
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: size, FileOptions.SequentialScan | FileOptions.Asynchronous);

            var buffer = new byte[size];
            var index = 0;
            long offset = 0;

            while (!cancellationToken.IsCancellationRequested)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, size), cancellationToken);
                if (read == 0) yield break;

                yield return new Chunk(index++, count, offset, buffer.AsMemory(0, read));
                offset += read;
            }
        }
    }
}
