using SecondScreen.Core.Protocol;
using Xunit;

namespace SecondScreen.Tests;

public class StreamFragmentationTests
{
    private sealed class ChunkedMemoryStream : Stream
    {
        private readonly byte[] _data;
        private readonly int _chunkSize;
        private int _position;

        public ChunkedMemoryStream(byte[] data, int chunkSize)
        {
            _data = data;
            _chunkSize = chunkSize;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _data.Length;
        public override long Position
        {
            get => _position;
            set => throw excitingNotSupported();
        }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position >= _data.Length) return 0;
            int toRead = Math.Min(Math.Min(count, _chunkSize), _data.Length - _position);
            Array.Copy(_data, _position, buffer, offset, toRead);
            _position += toRead;
            return toRead;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position >= _data.Length) return ValueTask.FromResult(0);
            int toRead = Math.Min(Math.Min(buffer.Length, _chunkSize), _data.Length - _position);
            _data.AsSpan(_position, toRead).CopyTo(buffer.Span);
            _position += toRead;
            return ValueTask.FromResult(toRead);
        }

        public override long Seek(long offset, SeekOrigin origin) => throw excitingNotSupported();
        public override void SetLength(long value) => throw excitingNotSupported();
        public override void Write(byte[] buffer, int offset, int count) => throw excitingNotSupported();

        private static NotSupportedException excitingNotSupported() => new();
    }

    [Fact]
    public async Task ReadPacketAsync_50KbVideoFrame_ChunkedIn1Kb_ReconstructsPayloadCompletely()
    {
        // 50 KB dummy video frame
        byte[] nalData = new byte[50 * 1024];
        new Random(42).NextBytes(nalData);

        var frame = new VideoFrameMessage(
            FrameNumber: 777,
            CaptureDurationUs: 1200,
            EncodeDurationUs: 2300,
            NalData: nalData,
            IsKeyframe: true);

        byte[] payload = new byte[frame.PayloadSize];
        frame.WritePayloadTo(payload);

        var header = FrameHeader.Create(
            MessageType.VideoFrame,
            (uint)payload.Length,
            sequence: 100,
            timestampUs: 555555,
            flags: frame.GetFlags());

        byte[] fullPacket = PacketWriter.SerializePacket(header, payload);

        // Feed via 1 KB chunked stream
        using var stream = new ChunkedMemoryStream(fullPacket, chunkSize: 1024);
        var (parsedHeader, parsedPayload) = await PacketReader.ReadPacketAsync(stream);

        Assert.Equal(MessageType.VideoFrame, parsedHeader.MsgType);
        Assert.Equal((uint)payload.Length, parsedHeader.PayloadLength);
        Assert.Equal((ushort)100, parsedHeader.Sequence);
        Assert.Equal((uint)555555, parsedHeader.TimestampUs);

        var parsedFrame = VideoFrameMessage.ReadPayloadFrom(parsedPayload, parsedHeader.Flags);
        Assert.Equal((uint)777, parsedFrame.FrameNumber);
        Assert.Equal((ushort)1200, parsedFrame.CaptureDurationUs);
        Assert.Equal((ushort)2300, parsedFrame.EncodeDurationUs);
        Assert.True(parsedFrame.IsKeyframe);
        Assert.Equal(nalData.Length, parsedFrame.NalData.Length);
        Assert.Equal(nalData, parsedFrame.NalData);
    }

    [Fact]
    public async Task ReadPacketAsync_ExtremeFragmentation_1ByteChunks_ReadsSuccessfully()
    {
        var ack = new HelloAckMessage(
            Status: ProtocolConstants.HelloStatusOk,
            CodecId: ProtocolConstants.CodecIdH264,
            StreamWidth: 1920,
            StreamHeight: 1080,
            TargetFps: 60,
            BitrateBps: 15_000_000,
            SessionId: 9999);

        byte[] payload = new byte[HelloAckMessage.PayloadSize];
        ack.WritePayloadTo(payload);

        var header = FrameHeader.Create(MessageType.HelloAck, (uint)payload.Length, sequence: 1);
        byte[] fullPacket = PacketWriter.SerializePacket(header, payload);

        // 1-byte chunks
        using var stream = new ChunkedMemoryStream(fullPacket, chunkSize: 1);
        var (parsedHeader, parsedPayload) = await PacketReader.ReadPacketAsync(stream);

        Assert.Equal(MessageType.HelloAck, parsedHeader.MsgType);
        var parsedAck = HelloAckMessage.ReadPayloadFrom(parsedPayload);
        Assert.Equal(1920, parsedAck.StreamWidth);
        Assert.Equal(1080, parsedAck.StreamHeight);
        Assert.Equal((uint)9999, parsedAck.SessionId);
    }

    [Fact]
    public async Task ReadPacketAsync_TruncatedStream_ThrowsEndOfStreamException()
    {
        byte[] incomplete = [0x53, 0x53, 0x01, 0x01, 0x00, 0x00, 0x00, 0x01]; // only 8 bytes instead of 16
        using var stream = new MemoryStream(incomplete);

        await Assert.ThrowsAsync<EndOfStreamException>(async () =>
        {
            await PacketReader.ReadPacketAsync(stream);
        });
    }
}

