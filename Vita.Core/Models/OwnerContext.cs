using Vita.Core.Services;

namespace Vita.Core.Models;

public sealed class OwnerContext
{
    public required VitaSourceItem Item { get; init; }

    public required VitaPfsFileTable Table { get; init; }

    public required WorkBinLicense License { get; init; }

    public required IVitaSourceAccessor WorkBinAccessor { get; init; }

    public required string WorkBinRelativePath { get; init; }
}