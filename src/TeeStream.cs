namespace TeeStreaming;

using System.Buffers;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// A utility to "tee" a source stream to one or more sink streams
/// </summary>
public class TeeStream : Stream
{
    public readonly static Stream Self = Stream.Null;

    protected internal IEnumerable<Stream> _sinks;
    protected internal RingBuffer? _outputBuffer;
    protected internal int _bufferSize;
    protected internal bool _firstByteReceived = false;
    protected internal bool _atEnd = false;

    protected long _totalBytesRead = 0;
    protected long _totalBytesWritten = 0;

    public const int DEFAULT_TEESTREAM_BUFFER_SIZE = 81920;

    /// <summary>
    /// Convenience constructor
    /// </summary>
    /// <param name="sinks"></param>
    public TeeStream(params Stream[] sinks) : this(DEFAULT_TEESTREAM_BUFFER_SIZE, sinks)
    {
    }

    /// <summary>
    /// Instantiate and configure a StreamTee
    /// </summary>
    /// <param name="sinks">Writable stream sinks</param>
    /// <param name="bufferSize">Size of buffer</param>
    /// <exception cref="ArgumentException"></exception>
    public TeeStream(int bufferSize = DEFAULT_TEESTREAM_BUFFER_SIZE, params Stream[] sinks)
    {
        var sinkCount = sinks.Length;
        if (sinkCount == 0)
        {
            throw new ArgumentException("There must be at least one sink");
        }
        if (bufferSize < 1024)
        {
            throw new ArgumentException("Buffer size must be at least 1,024");
        }

        var idx = 1;
        foreach (var sink in sinks)
        {
            if (sink == TeeStream.Self)
            {
                _outputBuffer = new RingBuffer(bufferSize);
            }
            else if (!sink.CanWrite)
            {
                throw new ArgumentException($"Sink #{idx} is not writable");
            }
            idx++;
        }
        _bufferSize = bufferSize;
        _sinks = sinks.Where(sink => sink != TeeStream.Self);
    }

    /// <summary>
    /// Close the Tee stream and any sink streams as well
    /// </summary>
    public override void Close()
    {
        base.Close();
        foreach (var sink in _sinks)
        {
            sink.Close();
        }
    }

    /// <summary>
    /// Tidy up
    /// </summary>
    /// <param name="disposing"></param>
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        _outputBuffer?.Dispose();
        _outputBuffer = null;
    }

    /// <summary>
    /// Copy from the source stream into our tee.  Unlike CopyTo, this allows us to know when the end of the stream is.
    /// We also use switch between two buffers to facilitate simultaneous read/write operations
    /// </summary>
    /// <param name="source"></param>
    /// <param name="bufferSize"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task CopyFromAsync(Stream source, CancellationToken? cancellationToken = null)
    {
        await Task.Run(async () =>
        {
            var holdBufferSize = _bufferSize / 2;
            var holdThreshold = holdBufferSize * 90 / 100;
            byte[] holdBufferA = ArrayPool<byte>.Shared.Rent(holdBufferSize);
            byte[] holdBufferB = ArrayPool<byte>.Shared.Rent(holdBufferSize);

            var bufferToRead = holdBufferA;
            var bufferToWrite = holdBufferB;

            var bufferToReadLength = 0;
            var bufferToWriteLength = 0;

            var tasks = new List<Task>(2);

            try
            {
                var token = cancellationToken.HasValue ? cancellationToken.Value : CancellationToken.None;

                int bytesRead;
                do
                {
                    var readTask = source.ReadAsync(bufferToRead, bufferToReadLength, holdBufferSize - bufferToReadLength, token);
                    tasks.Add(readTask);
                    if (bufferToWriteLength > 0)
                    {
                        tasks.Add(WriteAsync(bufferToWrite, 0, bufferToWriteLength, token));
                        bufferToWriteLength = 0;
                    }

                    Task.WaitAll(tasks.ToArray());

                    bytesRead = readTask.Result;
                    
                    bufferToReadLength += bytesRead;

                    if (bufferToReadLength > holdThreshold && bytesRead != 0)
                    {
                        var thisBuffer = (bufferToRead == holdBufferA) ? holdBufferA : holdBufferB;
                        var otherBuffer = (bufferToRead == holdBufferA) ? holdBufferB : holdBufferA;

                        bufferToWriteLength = bufferToReadLength;
                        bufferToWrite = thisBuffer;

                        bufferToReadLength = 0;
                        bufferToRead = otherBuffer;
                    }

                } while (bytesRead > 0);

                if (bufferToReadLength > 0) {
                    await WriteAsync(bufferToRead, 0, bufferToReadLength, token);
                }

                _atEnd = true;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(holdBufferA);
                ArrayPool<byte>.Shared.Return(holdBufferB);
            }
        }).ConfigureAwait(false);



        //     var holdBufferSize = _bufferSize;
        //     var holdBufferLength = 0;
        //     var holdThreshold = holdBufferSize * 90 / 100;
        //     byte[] holdBuffer = ArrayPool<byte>.Shared.Rent(holdBufferSize);

        //     try
        //     {
        //         var token = cancellationToken.HasValue ? cancellationToken.Value : CancellationToken.None;
        //         int bytesRead;
        //         while ((bytesRead = await source.ReadAsync(holdBuffer, holdBufferLength, holdBufferSize - holdBufferLength, token).ConfigureAwait(false)) != 0)
        //         {
        //             holdBufferLength += bytesRead;
        //             if (holdBufferLength > holdThreshold)
        //             {
        //                 await WriteAsync(holdBuffer, 0, holdBufferLength, token);
        //                 holdBufferLength = 0;
        //             }
        //         }
        //         if (holdBufferLength > 0)
        //         {
        //             await WriteAsync(holdBuffer, 0, holdBufferLength, token).ConfigureAwait(false);
        //         }
        //         _atEnd = true;
        //     }
        //     finally
        //     {
        //         ArrayPool<byte>.Shared.Return(holdBuffer);
        //     }
        // }).ConfigureAwait(false);
    }

    /// <summary>
    /// Call this after the final WriteAsync to indicate the end of an input stream
    /// </summary>
    public void SetAtEnd()
    {
        _atEnd = true;
    }

    /// <summary>
    /// Indicates stream can be read from if configured for self streaming
    /// </summary>
    public override bool CanRead => _outputBuffer != null;

    /// <summary>
    /// If configured for self streaming, copies content in the output buffer
    /// </summary>
    /// <param name="buffer"></param>
    /// <param name="offset"></param>
    /// <param name="count"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    /// <exception cref="NotImplementedException"></exception>
    public override Task<int> ReadAsync(Byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            if (_outputBuffer == null)
            {
                throw new NotImplementedException();
            }

            // System.Console.Error.WriteLine($"Read called with {count} bytes");
            // Make sure we we don't return zero because we haven't received the first
            // byte yet, or we have cleared out the buffer but have not yet received
            // the "at end" signal
            while ((!_firstByteReceived) || ((!_atEnd) && (_outputBuffer.Length == 0)))
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    throw new TaskCanceledException();
                }
                Thread.Yield();
            }

            var result = _outputBuffer.Read(buffer, offset, count);
            _totalBytesRead += result;
            // Console.Error.WriteLine($"Bytes Read: {result}, Length: {_outputBuffer.Length}, Total: {_totalBytesRead}");
            return result;
        });
    }

    /// <summary>
    /// Indicates that stream can be written to
    /// </summary>
    public override bool CanWrite { get => true; }

    /// <summary>
    /// Forwards the buffer to the Tee's sinks and, if configured, the Tee' self output
    /// </summary>
    /// <param name="buffer"></param>
    /// <param name="offset"></param>
    /// <param name="count"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            // System.Console.Error.WriteLine($"Write called for {count} bytes");
            // Write out data to any defined sinks
            Task.WaitAll(_sinks.Select(sink => sink.WriteAsync(buffer, offset, count, cancellationToken)).ToArray());

            // If self-outputing, then update buffer once there is room
            if (_outputBuffer != null)
            {
                var bytesRemaining = count;
                while (bytesRemaining > 0)
                {
                    int bytesToWrite;
                    do
                    {
                        bytesToWrite = Math.Min(_outputBuffer.Available, bytesRemaining);
                        if (bytesToWrite == 0)
                        {
                            if (cancellationToken.IsCancellationRequested)
                            {
                                throw new TaskCanceledException();
                            }
                            Thread.Yield();
                        }
                    } while (bytesToWrite < 1);
                    _outputBuffer.Write(buffer, offset, bytesToWrite);
                    offset += bytesToWrite;
                    _totalBytesWritten += bytesToWrite;
                    // Console.Error.WriteLine($"Bytes Written: {bytesToWrite}, Length: {_outputBuffer.Length}, Total: {_totalBytesWritten}");
                    bytesRemaining -= bytesToWrite;
                    _firstByteReceived = true;
                }
            }
        });
    }

    /// <summary>
    /// Flush any sink streams
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public override Task FlushAsync(CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            Task.WaitAll(_sinks.Select(sink =>
            {
                var task = sink.FlushAsync(cancellationToken);
                task.ConfigureAwait(false);
                return task;
            }).ToArray());
        });
    }

    /// <summary>
    /// Position is not implemented in TeeStream
    /// </summary>
    public override long Position
    {
        get => throw new NotImplementedException();
        set => throw new NotImplementedException();
    }

    /// <summary>
    /// Perform a blocking read of the stream
    /// </summary>
    /// <param name="buffer"></param>
    /// <param name="offset"></param>
    /// <param name="count"></param>
    /// <returns></returns>
    /// <exception cref="InvalidOperationException"></exception>
    public override int Read(byte[] buffer, int offset, int count)
    {
        var task = Task.Run(() => ReadAsync(buffer, offset, count, CancellationToken.None));
        task.Wait();
        return task.Result;
    }

    /// <summary>
    /// Perform a blocking write of the stream
    /// </summary>
    /// <param name="buffer"></param>
    /// <param name="offset"></param>
    /// <param name="count"></param>
    /// <exception cref="InvalidOperationException"></exception>
    public override void Write(byte[] buffer, int offset, int count)
    {
        Task.Run(() => WriteAsync(buffer, offset, count, CancellationToken.None)).Wait();
    }

    /// <summary>
    /// Perform a blocking flush of the stream
    /// </summary>
    /// <exception cref="InvalidOperationException"></exception>
    public override void Flush()
    {
        Task.Run(() => FlushAsync(CancellationToken.None)).Wait();
    }

    /// <summary>
    /// Length is not implemented in TeeStream
    /// </summary>
    public override long Length
    {
        get => throw new NotImplementedException();
    }

    /// <summary>
    /// SetLength is not implemented in TeeStream
    /// </summary>
    /// <param name="length"></param>
    /// <exception cref="NotImplementedException"></exception>
    public override void SetLength(long length)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Indicates that Seek is not implemented in TeeStream
    /// </summary>
    public override bool CanSeek { get => false; }

    /// <summary>
    /// Seek is not implemented in TeeStream
    /// </summary>
    /// <param name="offset"></param>
    /// <param name="origin"></param>
    /// <returns></returns>
    /// <exception cref="NotImplementedException"></exception>
    public override long Seek(long offset, SeekOrigin origin)
    {
        throw new NotImplementedException();
    }
}