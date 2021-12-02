using System.Buffers;
using NUnit.Framework;
using TeeStreaming;
using System.Threading;
using System.Threading.Tasks;
using System.IO;

namespace test;

class RingBufferTests
{
    [Test]
    public void WritesBytesSequentially()
    {
        using (var ringBuffer = new RingBuffer())
        {
            ringBuffer.Write(new byte[] { 1, 2, 3 }, 0, 3);
            Assert.AreEqual(3, ringBuffer._length);
            Assert.AreEqual(0, ringBuffer._anchor);
            ringBuffer.Write(new byte[] { 0, 4, 5 }, 1, 2);
            Assert.AreEqual(5, ringBuffer._length);
            Assert.AreEqual(0, ringBuffer._anchor);
            for (var i = 0; i < 5; i++)
            {
                Assert.AreEqual(i + 1, ringBuffer._buffer[i]);
            }
        }
    }

    [Test]
    public void ReadsBytesSequentially()
    {
        using (var ringBuffer = new RingBuffer())
        {
            ringBuffer.Write(new byte[] { 1, 2, 3, 4, 5 }, 0, 5);

            var hold = new byte[5];
            ringBuffer.Read(hold, 0, 3);
            Assert.AreEqual(2, ringBuffer._length);
            Assert.AreEqual(3, ringBuffer._anchor);
            Assert.AreEqual(new byte[] { 1, 2, 3, 0, 0 }, hold);
            ringBuffer.Read(hold, 3, 2);
            Assert.AreEqual(0, ringBuffer._length);
            Assert.AreEqual(0, ringBuffer._anchor);
            Assert.AreEqual(new byte[] { 1, 2, 3, 4, 5 }, hold);
        }
    }

    [Test]
    public void RingWraps()
    {
        using (var ringBuffer = new RingBuffer(32768))
        {
            var sampleLength = ringBuffer.Capacity * 120 / 100;
            var sampleData = new byte[sampleLength];
            for (var i = 0; i < sampleLength; i++)
            {
                sampleData[i] = (byte)(i % 255);
            }
            var hold = new byte[sampleLength];

            // Write the first half
            var firstChunkToWrite = sampleLength / 2;
            ringBuffer.Write(sampleData, 0, firstChunkToWrite);

            // Read the first three bytes of the first chunk (leaving at least two in there)
            var firstChunkToRead = sampleLength / 3;
            ringBuffer.Read(hold, 0, firstChunkToRead);
            Assert.AreEqual(firstChunkToWrite - firstChunkToRead, ringBuffer._length);
            Assert.AreEqual(firstChunkToRead, ringBuffer._anchor);

            // Write out everything else
            ringBuffer.Write(sampleData, firstChunkToWrite, sampleLength - firstChunkToWrite);
            Assert.AreEqual(sampleLength - firstChunkToRead, ringBuffer._length);
            Assert.AreEqual(firstChunkToRead, ringBuffer._anchor);

            // Read in all data from the ring, which should result in a "wrap"
            ringBuffer.Read(hold, firstChunkToRead, sampleLength - firstChunkToRead);
            Assert.AreEqual(sampleData, hold);
            Assert.AreEqual(0, ringBuffer._length);
            Assert.AreEqual(0, ringBuffer._anchor);
        }
    }

    [Test]
    public void FaultsOnOverflow()
    {
        using (var ringBuffer = new RingBuffer(10))
        {
            var sampleData = new byte[ringBuffer.Capacity * 2];
            Assert.Throws<InternalBufferOverflowException>(() => ringBuffer.Write(sampleData, 0, ringBuffer.Capacity * 2),
                $"Insufficent available space in buffer for {ringBuffer.Capacity * 2} more bytes");
        }
    }
    
    [Test]
    public void CapacityReturnsCapacity()
    {
        using (var ringBuffer = new RingBuffer(10))
        {
            Assert.AreEqual(ringBuffer._buffer.Length, ringBuffer.Capacity);
        }
    }

    [Test]
    public void LengthReturnsLength()
    {
        using (var ringBuffer = new RingBuffer(10))
        {
            var sampleData = new byte[5];
            ringBuffer.Write(sampleData, 0, 5);
            Assert.AreEqual(5, ringBuffer.Length);
        }
    }

    [Test]
    public void AvailableReturnsAvailable()
    {
        using (var ringBuffer = new RingBuffer(10))
        {
            var sampleData = new byte[5];
            ringBuffer.Write(sampleData, 0, 5);
            Assert.AreEqual(ringBuffer._buffer.Length - 5, ringBuffer.Available);
        }
    }

}