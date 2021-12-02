namespace TeeStreaming;

using System;
using System.Buffers;
using System.IO;
using System.Threading;

/// <summary>
/// This class is a ring-buffer of fixed capacity, it DOES NOT auto-expand.
/// It is meant to allow continuous reads and writes
/// of arbitrary lengths without having to reallocate and/or shift buffer contents.
/// </summary>
public class RingBuffer : IDisposable
{
    protected internal Mutex _mutex = new Mutex();
    protected internal byte[] _buffer;
    protected internal int _capacity;
    protected internal int _available;
    protected internal int _concurrencyTimeout;
    protected internal int _anchor = 0;
    protected internal int _length = 0;

    public int Length { get => _length; }
    public int Capacity { get => _capacity; }
    public int Available { get => _available; }


    /// <summary>
    /// Construct a ring buffer with the specified capacity and concurrency timeout
    /// </summary>
    /// <param name="capacity"></param>
    /// <param name="concurrencyTimeout"></param>
    public RingBuffer(int capacity = 131072, int concurrencyTimeout = -1)
    {
        // Grab our byte buffer from the ArrayPool
        _buffer = ArrayPool<byte>.Shared.Rent(capacity);
        // Note, Rent can return more than the requested capacity, might as well use it...
        _capacity = _buffer.Length;
        _available = capacity;
        _concurrencyTimeout = concurrencyTimeout;
        // _locker = new SemaphoreLocker(_concurrencyTimeout);
    }

    /// <summary>
    /// Dispose of the byte buffer
    /// </summary>
    public void Dispose()
    {
        ArrayPool<byte>.Shared.Return(_buffer);
    }

    /// <summary>
    /// Read up to the specified number of bytes from the buffer
    /// </summary>
    /// <param name="destination"></param>
    /// <param name="offset"></param>
    /// <param name="count"></param>
    /// <returns></returns>
    public int Read(byte[] destination, int offset, int count)
    {
        try
        {
            _mutex.WaitOne(_concurrencyTimeout);
            var bytesCopied = 0;
            var bytesRemaining = Math.Min(_length, count);

            if (bytesRemaining > 0)
            {
                // Step #1, retrieve any data that fits between the current anchor and the end of the buffer
                // Step #2, if the anchor was in the middle of the buffer, we may have remaining data to retrieve
                for (var loop = 0; loop < 2 && bytesRemaining > 0; loop++)
                {
                    var bytesToCopy = Math.Min(bytesRemaining, _capacity - _anchor);

                    Array.Copy(_buffer, _anchor, destination, offset + bytesCopied, bytesToCopy);
                    bytesCopied += bytesToCopy;
                    _length -= bytesToCopy;
                    bytesRemaining -= bytesToCopy;

                    if (_anchor + bytesCopied >= _capacity)
                    {
                        _anchor = 0;
                    }
                    else
                    {
                        _anchor += bytesToCopy;
                    }
                }

                _available = _capacity - _length;

                // Reset anchor to zero if the buffer is empty
                if (_length == 0 && _anchor != 0)
                {
                    _anchor = 0;
                }
            }

            return bytesCopied;
        }
        finally
        {
            _mutex.ReleaseMutex();
        }
    }

    /// <summary>
    /// Write "count" bytes to the buffer.  Will throw an overflow exception if there
    /// is not enough room in the ring buffer to store the data
    /// </summary>
    /// <param name="source"></param>
    /// <param name="offset"></param>
    /// <param name="count"></param>
    /// <exception cref="InternalBufferOverflowException"></exception>
    public void Write(byte[] source, int offset, int count)
    {
        try
        {
            _mutex.WaitOne(_concurrencyTimeout);

            if (count > _available)
            {
                throw new InternalBufferOverflowException($"Insufficent available space in buffer for {count} more bytes");
            }

            var bytesRemaining = count;
            var bytesCopied = 0;

            // Step #1, write any data that fits between the current anchor and the end of the buffer
            // Step #2, if the anchor was in the middle of the buffer, we may have remaining data to write
            for (var loop = 0; loop < 2 && bytesRemaining > 0; loop++)
            {
                int position;
                int bytesToCopy;
                if (loop == 0)
                {
                    position = _anchor + _length;
                    bytesToCopy = Math.Min(bytesRemaining, _capacity - position);
                }
                else
                {
                    position = 0;
                    bytesToCopy = Math.Min(bytesRemaining, _anchor);
                }

                if (bytesToCopy > 0)
                {
                    Array.Copy(source, offset + bytesCopied, _buffer, position, bytesToCopy);
                    bytesCopied += bytesToCopy;
                    _length += bytesToCopy;
                    bytesRemaining -= bytesToCopy;
                }
            }

            _available = _capacity - _length;
        }
        finally
        {
            _mutex.ReleaseMutex();
        }
    }
}