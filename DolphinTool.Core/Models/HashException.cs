namespace DolphinTool.Core.Models;

internal readonly record struct HashException(ushort Offset, byte[] Hash);