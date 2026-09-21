namespace DolphinTool.Core.Models;

internal readonly record struct RawDataEntry(long DataOffset, long DataSize, uint GroupIndex, uint GroupCount);