namespace DolphinTool.Core.Rvz;

internal enum RvzCompressionType : uint
{
    None = 0,
    Purge = 1,
    Bzip2 = 2,
    Lzma = 3,
    Lzma2 = 4,
    Zstd = 5
}

internal readonly record struct HashException(ushort Offset, byte[] Hash);

internal readonly record struct PartitionDataEntry(uint FirstSector, uint SectorCount, uint GroupIndex, uint GroupCount);

internal readonly record struct RawDataEntry(long DataOffset, long DataSize, uint GroupIndex, uint GroupCount);

internal readonly record struct GroupEntry(uint DataOffset4, uint DataSizeField, uint RvzPackedSize)
{
    public bool IsCompressed => (DataSizeField & 0x80000000u) != 0;

    public int DataSize => (int)(DataSizeField & 0x7FFFFFFFu);

    public long FileOffset => (long)DataOffset4 << 2;
}

internal sealed class PartitionEntry
{
    public required byte[] Key { get; init; }

    public required PartitionDataEntry[] DataEntries { get; init; }

    public uint FirstSector => DataEntries[0].FirstSector;

    public long TotalSectors => DataEntries[1].SectorCount != 0
        ? (long)DataEntries[1].FirstSector - DataEntries[0].FirstSector + DataEntries[1].SectorCount
        : DataEntries[0].SectorCount;
}
