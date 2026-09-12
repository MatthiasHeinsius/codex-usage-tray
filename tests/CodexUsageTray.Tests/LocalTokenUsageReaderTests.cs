namespace CodexUsageTray.Tests;

public sealed class LocalTokenUsageReaderTests
{
    [Fact]
    public void CopyUnreadBytesReturnsTheConsumedSourcePosition()
    {
        using var source = new LengthAheadStream(length: 10, position: 3);
        using var destination = new MemoryStream();

        var position = LocalTokenUsageReader.CopyUnreadBytes(source, destination);

        Assert.Equal(3, position);
    }

    [Fact]
    public void SumLinesForDateIncludesOnlyTokenUsageFromTheRequestedLocalDate()
    {
        var lines = new[]
        {
            "{\"timestamp\":\"2026-09-07T08:00:00Z\",\"type\":\"token_usage_record\",\"payload\":{\"usage\":{\"total_tokens\":1200}}}",
            "{\"timestamp\":\"2026-09-07T09:00:00Z\",\"type\":\"token_usage_record\",\"payload\":{\"usage\":{\"total_tokens\":800}}}",
            "{\"timestamp\":\"2026-09-06T09:00:00Z\",\"type\":\"token_usage_record\",\"payload\":{\"usage\":{\"total_tokens\":9999}}}",
            "{\"timestamp\":\"2026-09-07T09:00:00Z\",\"type\":\"event_msg\",\"payload\":{}}"
        };

        var result = LocalTokenUsageReader.SumLinesForDate(lines, new DateOnly(2026, 9, 7));

        Assert.True(result.Found);
        Assert.Equal(2_000, result.Tokens);
    }

    private sealed class LengthAheadStream(long length, long position) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get; set; } = position;

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => 0;
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
