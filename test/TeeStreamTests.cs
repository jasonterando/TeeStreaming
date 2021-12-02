using System.Buffers;
using NUnit.Framework;
using TeeStreaming;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using System;
using Moq;

namespace test;

class TeeStreamTests
{
    [Test]
    public void CanCreateWithDefaults()
    {
        using (var testStream = new MemoryStream(1))
        {
            using (var tee = new TeeStream(testStream))
            {
                Assert.AreEqual(81920, tee._bufferSize);
                Assert.Null(tee._outputBuffer);
                Assert.AreEqual(new Stream[] { testStream }, tee._sinks);
            }
        }
    }

    [Test]
    public void CanCreateWithSelf()
    {
        using (var testStream = new MemoryStream(1))
        {
            using (var tee = new TeeStream(TeeStream.Self, testStream))
            {
                Assert.AreEqual(81920, tee._bufferSize);
                Assert.NotNull(tee._outputBuffer);
                Assert.AreEqual(new Stream[] { testStream }, tee._sinks);
            }
        }
    }

    [Test]
    public void CreateFaultsOnNoSinks()
    {
        Assert.Throws<ArgumentException>(() => new TeeStream(new Stream[] { }),
            "There must be at least one sink");
    }

    [Test]
    public void CreateFaultsOnSmallBuffer()
    {
        Assert.Throws<ArgumentException>(() => new TeeStream(128, TeeStream.Self),
            "Buffer size must be at least 1,024");
    }

    [Test]
    public void CreateFaultsOnWritableSink()
    {
        var sink = new Mock<Stream>();
        sink.SetupGet(s => s.CanWrite).Returns(false);
        Assert.Throws<ArgumentException>(() => new TeeStream(sink.Object),
            "Sink #1 is not writable");
    }

    [Test]
    public void CloseCallsSinkClose()
    {
        var sink = new Mock<Stream>();
        sink.SetupGet(s => s.CanWrite).Returns(true);
        sink.Setup(s => s.Close()).Verifiable();
        using (var tee = new TeeStream(sink.Object))
        {
            tee.Close();
            sink.Verify(s => s.Close(), Times.Once);
        }
    }

    [Test]
    public void DisposeDisposeesOfOutputBuffer()
    {
        var tee = new TeeStream(TeeStream.Self);
        Assert.NotNull(tee._outputBuffer);
        tee.Dispose();
        Assert.Null(tee._outputBuffer);
    }

    [Test]
    public async Task CopyFromAsyncReadsAndWritesEntireStreamWithSmallBuffer()
    {
        var sampleBuffer = new byte[4096];
        var rnd = new Random();
        rnd.NextBytes(sampleBuffer);

        using (var source = new MemoryStream(sampleBuffer))
        using (var destination = new MemoryStream(sampleBuffer.Length))
        using (var tee = new TeeStream(sampleBuffer.Length / 4, destination))
        {
            source.Position = 0;
            await tee.CopyFromAsync(source);

            destination.Position = 0;
            var results = new byte[sampleBuffer.Length];
            await destination.ReadAsync(results, 0, sampleBuffer.Length);
            Assert.AreEqual(sampleBuffer, results);
            Assert.AreEqual(sampleBuffer.Length, destination.Length);
        }
    }

    [Test]
    public async Task CopyFromAsyncReadsAndWritesEntireStreamWithLargeBuffer()
    {
        var sampleBuffer = new byte[1024];
        var rnd = new Random();
        rnd.NextBytes(sampleBuffer);

        using (var source = new MemoryStream(sampleBuffer))
        using (var destination = new MemoryStream(sampleBuffer.Length))
        using (var tee = new TeeStream(sampleBuffer.Length * 2, destination))
        {
            source.Position = 0;
            await tee.CopyFromAsync(source);

            destination.Position = 0;
            var results = new byte[sampleBuffer.Length];
            await destination.ReadAsync(results, 0, sampleBuffer.Length);
            Assert.AreEqual(sampleBuffer, results);
            Assert.AreEqual(sampleBuffer.Length, destination.Length);
        }
    }

    [Test]
    public async Task CopyFromAsyncReadsAndWritesEntireStreamWithSmallData()
    {
        var sampleBuffer = new byte[128];
        var rnd = new Random();
        rnd.NextBytes(sampleBuffer);

        using (var source = new MemoryStream(sampleBuffer))
        using (var destination = new MemoryStream(sampleBuffer.Length))
        using (var tee = new TeeStream(destination))
        {
            source.Position = 0;
            await tee.CopyFromAsync(source);

            destination.Position = 0;
            var results = new byte[sampleBuffer.Length];
            await destination.ReadAsync(results, 0, sampleBuffer.Length);
            Assert.AreEqual(sampleBuffer, results);
            Assert.AreEqual(sampleBuffer.Length, destination.Length);
        }
    }    

    [Test]
    public void CopyFromAsyncCancels()
    {
        var sampleBuffer = new byte[1024];
        var rnd = new Random();
        rnd.NextBytes(sampleBuffer);

        var cancel = new CancellationTokenSource();

        using (var source = new MemoryStream(sampleBuffer))
        using (var destination = new MemoryStream(sampleBuffer.Length))
        using (var tee = new TeeStream(sampleBuffer.Length * 2, destination))
        {
            source.Position = 0;
            cancel.Cancel();
            Assert.CatchAsync<AggregateException>(async () => await tee.CopyFromAsync(source, cancel.Token));
        }
    }

    [Test]
    public void SetAtEndSetsValue()
    {
        using (var tee = new TeeStream(TeeStream.Self))
        {
            tee.SetAtEnd();
            Assert.AreEqual(true, tee._atEnd);
        }
    }

    [Test]
    public void CanReadTrueIfSelfBuffering()
    {
        using (var tee = new TeeStream(TeeStream.Self))
        {
            Assert.AreEqual(true, tee.CanRead);
        }
    }

    [Test]
    public void CanReadFalseIfNotSelfBuffering()
    {
        using (var source = new MemoryStream(1))
        using (var tee = new TeeStream(source))
        {
            Assert.AreEqual(false, tee.CanRead);
        }
    }

    [Test]
    public void ReadAsyncThrowsIfNotSelfBuffering()
    {
        using (var source = new MemoryStream(1))
        using (var tee = new TeeStream(source))
        {
            var buffer = new byte[1];
            Assert.ThrowsAsync<NotImplementedException>(async () => await tee.ReadAsync(buffer, 0, 1));
        }
    }

    [Test]
    public void ReadAsyncThrowsIfCancelled()
    {
        using (var tee = new TeeStream(TeeStream.Self))
        {
            var cancel = new CancellationTokenSource();
            cancel.Cancel();
            var buffer = new byte[1];
            Assert.ThrowsAsync<TaskCanceledException>(async () => await tee.ReadAsync(buffer, 0, 1, cancel.Token));
        }
    }

    [Test]
    public async Task ReadAsyncWaitsForFirstByte()
    {
        using (var tee = new TeeStream(TeeStream.Self))
        {
            var source = new byte[1] { 3 };
            var result = new byte[1];
            var taskRead = tee.ReadAsync(result, 0, 1);
            await tee.WriteAsync(source, 0, 1);
            taskRead.Wait();
            Assert.AreEqual(source, result);
            Assert.AreEqual(1, taskRead.Result);
        }
    }

    [Test]
    public void ReadAsyncDoesNotReturnZeroUntilAtEndIsTriggered()
    {
        using (var tee = new TeeStream(TeeStream.Self))
        {
            var result = new byte[1];
            var taskRead = tee.ReadAsync(result, 0, 1);
            tee._firstByteReceived = true;
            tee._atEnd = true;
            taskRead.Wait();
            Assert.AreEqual(0, taskRead.Result);
        }
    }

    [Test]
    public void CanWriteReturnsTrue()
    {
        using (var tee = new TeeStream(TeeStream.Self))
        {
            Assert.AreEqual(true, tee.CanWrite);
        }
    }

    [Test]
    public async Task WriteAsyncWritesToSinks()
    {
        var sample = new byte[10] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 };
        using (var sink1 = new MemoryStream(10))
        using (var sink2 = new MemoryStream(10))
        using (var tee = new TeeStream(sink1, sink2))
        {
            await tee.WriteAsync(sample, 0, 10);

            var result = new byte[10];
            sink1.Position = 0;
            await sink1.ReadAsync(result, 0, 10);
            Assert.AreEqual(sample, result);
            sink2.Position = 0;
            await sink2.ReadAsync(result, 0, 10);
            Assert.AreEqual(sample, result);
        }
    }

    [Test]
    public async Task WriteAsyncSetsFirstByteReceived()
    {
        using (var tee = new TeeStream(TeeStream.Self))
        {
            Assert.False(tee._firstByteReceived);
            await tee.WriteAsync(new byte[] { 1 }, 0, 1);
            Assert.True(tee._firstByteReceived);
        }
    }

    [Test]
    public async Task WriteAsyncWaitsForOutputBuffer()
    {
        var sample = new byte[2048];
        var rnd = new Random();
        rnd.NextBytes(sample);

        using (var tee = new TeeStream(1024, TeeStream.Self))
        {
            // Write first block
            await tee.WriteAsync(sample, 0, 1024);

            // Write second block, which will not fit in buffer until we read
            var taskWrite2 = tee.WriteAsync(sample, 1024, 1024);
            var result = new byte[2048];
            await tee.ReadAsync(result, 0, 1024);
            taskWrite2.Wait();

            // Read and validate remainder
            await tee.ReadAsync(result, 1024, 2048);
            Assert.AreEqual(sample, result);
        }
    }

    [Test]
    public async Task WriteAsyncThrowsIfCancelled()
    {
        var cancel = new CancellationTokenSource();
        cancel.Cancel();

        var sample = new byte[2048];
        var rnd = new Random();
        rnd.NextBytes(sample);

        using (var tee = new TeeStream(1024, TeeStream.Self))
        {
            await tee.WriteAsync(sample, 0, 1024);
            Assert.ThrowsAsync<TaskCanceledException>(async () =>
                await tee.WriteAsync(sample, 1024, 1024, cancel.Token));
        }
    }

    [Test]
    public async Task FlushAsyncCallsSinkAsync()
    {
        var sink1 = new Mock<MemoryStream>();
        var sink2 = new Mock<MemoryStream>();
        sink1.CallBase = true;
        sink2.CallBase = true;
        sink1.SetupGet(s => s.CanWrite).Returns(true);
        sink2.SetupGet(s => s.CanWrite).Returns(true);
        sink1.Setup(s => s.Flush()).Verifiable();
        sink2.Setup(s => s.Flush()).Verifiable();
        using (var tee = new TeeStream(sink1.Object, sink2.Object))
        {
            await tee.FlushAsync();
            sink1.Verify(s => s.Flush(), Times.Once);
            sink2.Verify(s => s.Flush(), Times.Once);
        }
    }

    [Test]
    public void PositionThrowsNotImplemented()
    {
        using (var tee = new TeeStream(TeeStream.Self))
        {
            long x;
            Assert.Throws<NotImplementedException>(() => x = tee.Position);
            Assert.Throws<NotImplementedException>(() => tee.Position = 1);
        }
    }

    [Test]
    public async Task ReadCallsReadAsync()
    {
        var tee = new Mock<TeeStream>(TeeStream.Self);
        tee.CallBase = true;
        var buffer = new byte[1] { 1 };
        await tee.Object.WriteAsync(buffer, 0, 1);
        tee.Setup(t => t.ReadAsync(It.Is<byte[]>(b => b.Equals(buffer)),
            It.Is<int>(i => i.Equals(0)),
            It.Is<int>(i => i.Equals(1)),
            It.Is<CancellationToken>(c => c.Equals(CancellationToken.None)))).Verifiable();
        tee.Object.Read(buffer, 0, 1);
        tee.Verify(t => t.ReadAsync(It.Is<byte[]>(b => b.Equals(buffer)),
            It.Is<int>(i => i.Equals(0)),
            It.Is<int>(i => i.Equals(1)),
            It.Is<CancellationToken>(c => c.Equals(CancellationToken.None))), Times.Once);
    }

    [Test]
    public void WriteCallsWriteAsync()
    {
        var tee = new Mock<TeeStream>(TeeStream.Self);
        tee.CallBase = true;
        var buffer = new byte[1] { 1 };
        tee.Setup(t => t.WriteAsync(It.Is<byte[]>(b => b.Equals(buffer)),
            It.Is<int>(i => i.Equals(0)),
            It.Is<int>(i => i.Equals(1)),
            It.Is<CancellationToken>(c => c.Equals(CancellationToken.None)))).Verifiable();
        tee.Object.Write(buffer, 0, 1);
        tee.Verify(t => t.WriteAsync(It.Is<byte[]>(b => b.Equals(buffer)),
            It.Is<int>(i => i.Equals(0)),
            It.Is<int>(i => i.Equals(1)),
            It.Is<CancellationToken>(c => c.Equals(CancellationToken.None))), Times.Once);
    }

    [Test]
    public void FlushCallsFlushAsync()
    {
        var tee = new Mock<TeeStream>(TeeStream.Self);
        tee.CallBase = true;
        tee.Setup(t => t.FlushAsync(It.Is<CancellationToken>(c => c.Equals(CancellationToken.None)))).Verifiable();
        tee.Object.Flush();
        tee.Verify(t => t.FlushAsync(It.Is<CancellationToken>(c => c.Equals(CancellationToken.None))), Times.Once);
    }

    [Test]
    public void LengthThrowsNotImplemented()
    {
        using (var tee = new TeeStream(TeeStream.Self))
        {
            long x;
            Assert.Throws<NotImplementedException>(() => x = tee.Length);
        }
    }

    [Test]
    public void SetLengthThrowsNotImplemented()
    {
        using (var tee = new TeeStream(TeeStream.Self))
        {
            Assert.Throws<NotImplementedException>(() => tee.SetLength(0));
        }
    }


    [Test]
    public void CanSeekReturnsFalse()
    {
        using (var tee = new TeeStream(TeeStream.Self))
        {
            Assert.False(tee.CanSeek);
        }
    }

    [Test]
    public void SeekThrowsNotImplemented()
    {
        using (var tee = new TeeStream(TeeStream.Self))
        {
            Assert.Throws<NotImplementedException>(() => tee.Seek(0, SeekOrigin.Begin));
        }
    }
}