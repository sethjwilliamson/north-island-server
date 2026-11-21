namespace NorthIsland.Protocol.Framing;

/// <summary>Collects the bytes read from a socket and cuts them into frame payloads.</summary>
public sealed class FrameDecoder
{
    /// <summary>The decoded bytes not yet consumed.</summary>
    private byte[] _buffer = new byte[4096];

    /// <summary>The number of valid bytes in the buffer, counted from its start.</summary>
    private int _count;

    /// <summary>The offset of the first byte not yet consumed; the bytes before it are dropped when the next bytes are fed.</summary>
    private int _start;

    /// <summary>The number of bytes dropped while looking for a frame header.</summary>
    public long SkippedBytes { get; private set; }

    /// <summary>Appends bytes read from the socket, decoding them.</summary>
    /// <param name="raw">The bytes as received.</param>
    public void Feed(ReadOnlySpan<byte> raw)
    {
        Compact();
        EnsureCapacity(_count + raw.Length);
        var dest = _buffer.AsSpan(_count, raw.Length);
        raw.CopyTo(dest);
        FrameCodec.Xor(dest);
        _count += raw.Length;
    }

    /// <summary>
    /// Takes the next complete frame's payload out of the buffer, dropping two bytes at a time while the header is not valid. Consuming only moves
    /// an offset, so skipping junk costs no copy per skipped pair; the buffer is compacted once, when the next bytes are fed.
    /// </summary>
    /// <returns>The payload, or null when no complete frame is buffered.</returns>
    public byte[]? NextPayload()
    {
        while (_count - _start >= FrameCodec.HeaderSize)
        {
            if (_buffer[_start] != FrameCodec.Magic0 || _buffer[_start + 1] != FrameCodec.Magic1)
            {
                _start += 2; // the header is looked for two bytes at a time
                SkippedBytes += 2;
                continue;
            }

            var length = _buffer[_start + 2] | (_buffer[_start + 3] << 8); // u16 little-endian length after the two magic bytes
            if (_count - _start - FrameCodec.HeaderSize < length)
                break;

            var payload = _buffer.AsSpan(_start + FrameCodec.HeaderSize, length).ToArray();
            _start += FrameCodec.HeaderSize + length;
            return payload;
        }

        return null;
    }

    /// <summary>Moves the bytes not yet consumed to the start of the buffer.</summary>
    private void Compact()
    {
        if (_start == 0)
            return;
        _buffer.AsSpan(_start, _count - _start).CopyTo(_buffer);
        _count -= _start;
        _start = 0;
    }

    /// <summary>Grows the buffer so that it holds at least a number of bytes.</summary>
    /// <param name="needed">The number of bytes needed.</param>
    private void EnsureCapacity(int needed)
    {
        if (needed <= _buffer.Length)
            return;
        Array.Resize(ref _buffer, Math.Max(needed, _buffer.Length * 2));
    }
}
