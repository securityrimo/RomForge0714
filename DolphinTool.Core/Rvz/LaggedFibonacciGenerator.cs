using System.Buffers.Binary;

namespace DolphinTool.Core.Rvz;

internal sealed class LaggedFibonacciGenerator
{
    public const int SeedBytes = 17 * sizeof(uint);

    private const int SeedSize = 17;
    private const int K = 521;
    private const int J = 32;
    private const int BufferBytes = K * sizeof(uint);

    private readonly uint[] _words = new uint[K];
    private readonly byte[] _bytes = new byte[BufferBytes];
    private int _positionBytes;
    private bool _dirty;

    public void SetSeed(ReadOnlySpan<byte> seed)
    {
        _positionBytes = 0;

        for (int i = 0; i < SeedSize; i++)
            _words[i] = BinaryPrimitives.ReadUInt32BigEndian(seed[(i * sizeof(uint))..]);

        for (int i = SeedSize; i < K; i++)
            _words[i] = (_words[i - 17] << 23) ^ (_words[i - 16] >> 9) ^ _words[i - 1];

        for (int i = 0; i < K; i++)
        {
            uint x = _words[i];
            _words[i] = (x & 0xFF00FFFFu) | ((x >> 2) & 0x00FF0000u);
        }

        for (int i = 0; i < 4; i++)
            Step();

        _dirty = true;
    }

    public void Forward(long count)
    {
        _positionBytes += (int)count;

        while (_positionBytes >= BufferBytes)
        {
            Step();
            _positionBytes -= BufferBytes;
        }

        _dirty = true;
    }

    public void GetBytes(Span<byte> destination)
    {
        if (_dirty)
            Refresh();

        int written = 0;
        while (written < destination.Length)
        {
            int length = Math.Min(destination.Length - written, BufferBytes - _positionBytes);
            _bytes.AsSpan(_positionBytes, length).CopyTo(destination[written..]);
            _positionBytes += length;
            written += length;

            if (_positionBytes == BufferBytes)
            {
                Step();
                Refresh();
                _positionBytes = 0;
            }
        }
    }

    private void Step()
    {
        for (int i = 0; i < J; i++)
            _words[i] ^= _words[i + K - J];

        for (int i = J; i < K; i++)
            _words[i] ^= _words[i - J];
    }

    private void Refresh()
    {
        for (int i = 0; i < K; i++)
            BinaryPrimitives.WriteUInt32BigEndian(_bytes.AsSpan(i * sizeof(uint)), _words[i]);

        _dirty = false;
    }
}
