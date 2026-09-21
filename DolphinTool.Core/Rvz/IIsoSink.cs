namespace DolphinTool.Core.Rvz;

internal interface IIsoSink
{
    void SetLength(long length);

    void Write(long offset, ReadOnlySpan<byte> data);
}