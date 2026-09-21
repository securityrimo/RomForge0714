using DolphinTool.Core.Models;
using System.Buffers.Binary;

namespace DolphinTool.Core.Rvz;

public static class DiscImageInspector
{
    public static DiscPlatform Detect(string path)
    {
        using var source = RvzInputSource.Open(path);

        if (source.Length < 0x20)
            return DiscPlatform.Unknown;

        Span<byte> header = stackalloc byte[0x20];
        source.Read(0, header);

        if (BinaryPrimitives.ReadUInt32BigEndian(header[0x18..]) == 0x5D1C9EA3)
            return DiscPlatform.Wii;

        if (BinaryPrimitives.ReadUInt32BigEndian(header[0x1C..]) == 0xC2339F3D)
            return DiscPlatform.GameCube;

        return DiscPlatform.Unknown;
    }
}