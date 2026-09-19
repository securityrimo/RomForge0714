using Common;
using Patch.Core;
using System.IO.Compression;
using Vita.Core.Models;

namespace Vita.Core.Services;

internal static class VitaPatchShared
{
    private const int ProgressChunkSize = 4 * 1024 * 1024;

    public static readonly HashSet<string> PatchExtensions = new(StringComparer.OrdinalIgnoreCase) { ".xdelta", ".xdelta3", ".ips", ".ups", ".bps", ".ppf", ".aps" };

    public static async Task<T> RunWithItemsAsync<T>(List<VitaBatchSourceEntry> entries, Action<string, LogLevel> log, Func<List<VitaSourceItem>, Task<T>> run)
    {
        var (items, ownedAccessors) = LoadItems(entries, log);

        try
        {
            return await run(items);
        }
        finally
        {
            foreach (var accessor in ownedAccessors)
                accessor.Dispose();
        }
    }

    public static (List<VitaSourceItem> Items, List<IVitaSourceAccessor> OwnedAccessors) LoadItems(List<VitaBatchSourceEntry> entries, Action<string, LogLevel> log)
    {
        var ownedAccessors = new List<IVitaSourceAccessor>();
        var items = new List<VitaSourceItem>();
        var appSourceByTitle = new Dictionary<string, (IVitaSourceAccessor Accessor, string SourcePath)>(StringComparer.OrdinalIgnoreCase);
        var orderedEntries = entries.OrderBy(GetEntrySortRank).ToList();

        void AddItem(VitaContentCategory category, string titleId, string? contentIdSuffix, string sourcePath, IVitaSourceAccessor accessor, string? patchPath)
        {
            items.Add(new VitaSourceItem
            {
                Category = category,
                TitleId = titleId,
                ContentIdSuffix = contentIdSuffix,
                SourcePath = sourcePath,
                Accessor = accessor,
                PatchPathOverride = patchPath
            });

            if (category == VitaContentCategory.App)
                appSourceByTitle[titleId] = (accessor, sourcePath);
        }

        foreach (var entry in orderedEntries)
        {
            if (entry.Kind == VitaSourceKind.Pkg)
            {
                if (entry.Probe is null)
                    throw new InvalidOperationException($"PKG 항목에 Probe 정보가 없습니다: {entry.Path}");

                IVitaSourceAccessor accessor;

                if (entry.Probe.Category == VitaContentCategory.Patch && string.IsNullOrWhiteSpace(entry.License) && appSourceByTitle.TryGetValue(entry.Probe.TitleId, out var appSource))
                {
                    string appWorkBinRel = $"{appSource.SourcePath}/sce_sys/package/work.bin";
                    var appLicense = WorkBinReader.Read(appSource.Accessor, appWorkBinRel);

                    accessor = new PkgSourceAccessor(entry.Path, appLicense.Klicensee);

                    log($"[patch] {entry.Probe.TitleId}: app의 라이선스를 그대로 공유해서 적용함", LogLevel.Info);
                }
                else
                {
                    accessor = new PkgSourceAccessor(entry.Path, entry.License);
                }

                ownedAccessors.Add(accessor);

                AddItem(entry.Probe.Category, entry.Probe.TitleId, entry.Probe.ContentIdSuffix, string.Empty, accessor, entry.PatchPath);
            }
            else
            {
                var accessor = VitaSourceAccessorFactory.Open(entry.Path);

                ownedAccessors.Add(accessor);

                if (entry.ItemSourcePath != null)
                {
                    var itemCategory = entry.ItemCategory ?? throw new InvalidOperationException($"항목 카테고리가 없습니다: {entry.Path}");
                    var itemTitleId = entry.ItemTitleId ?? throw new InvalidOperationException($"항목 TitleId가 없습니다: {entry.Path}");

                    AddItem(itemCategory, itemTitleId, entry.ItemContentIdSuffix, entry.ItemSourcePath, accessor, entry.PatchPath);
                }
                else
                {
                    foreach (var discovered in VitaSourcePreparer.DiscoverItems(accessor))
                        AddItem(discovered.Category, discovered.TitleId, discovered.ContentIdSuffix, discovered.SourcePath, accessor, entry.PatchPath);
                }
            }
        }

        return (items, ownedAccessors);
    }

    public static List<MergeGroup> BuildGroups(List<VitaSourceItem> items, string defaultPatchPath, Dictionary<string, PatchContext> patchContexts, Action<string, LogLevel> log, CancellationToken ct)
    {
        var appByTitle = items.Where(i => i.Category == VitaContentCategory.App).ToDictionary(i => i.TitleId, StringComparer.OrdinalIgnoreCase);
        var patchByTitle = items.Where(i => i.Category == VitaContentCategory.Patch).ToDictionary(i => i.TitleId, StringComparer.OrdinalIgnoreCase);
        var addcontItems = items.Where(i => i.Category == VitaContentCategory.Addcont).ToList();
        var titleIds = appByTitle.Keys.Union(patchByTitle.Keys, StringComparer.OrdinalIgnoreCase).ToList();
        var groups = new List<MergeGroup>();

        foreach (var titleId in titleIds)
        {
            ct.ThrowIfCancellationRequested();

            appByTitle.TryGetValue(titleId, out var appItem);
            patchByTitle.TryGetValue(titleId, out var patchItem);

            string? appWorkBinFallback = appItem != null ? $"{appItem.SourcePath}/sce_sys/package/work.bin" : null;
            var appOwner = appItem != null ? BuildOwnerContext(appItem, null, log) : null;
            var patchOwner = patchItem != null ? BuildOwnerContext(patchItem, appWorkBinFallback, log) : null;
            var owners = new List<OwnerContext>();

            if (appOwner != null)
                owners.Add(appOwner);

            if (patchOwner != null)
                owners.Add(patchOwner);

            if (owners.Count == 0)
                continue;

            string patchPackagePath = (patchItem?.PatchPathOverride ?? appItem?.PatchPathOverride) ?? defaultPatchPath;

            groups.Add(CreateGroup(VitaContentCategory.App, titleId, null, patchPackagePath, BuildPatchAppIndex(appOwner, patchOwner), owners, patchContexts, log));
        }

        foreach (var addcontItem in addcontItems)
        {
            ct.ThrowIfCancellationRequested();

            var owner = BuildOwnerContext(addcontItem, null, log);

            if (owner is null)
                continue;

            string patchPackagePath = addcontItem.PatchPathOverride ?? defaultPatchPath;

            groups.Add(CreateGroup(VitaContentCategory.Addcont, addcontItem.TitleId, addcontItem.ContentIdSuffix, patchPackagePath, BuildPatchAppIndex(owner, null), [owner], patchContexts, log));
        }

        return groups;
    }

    private static MergeGroup CreateGroup(VitaContentCategory category, string titleId, string? contentIdSuffix, string patchPackagePath, Dictionary<string, PatchAppEntry> index, List<OwnerContext> owners, Dictionary<string, PatchContext> patchContexts, Action<string, LogLevel> log)
    {
        var patchCtx = GetPatchContext(patchContexts, patchPackagePath, log);
        var targets = BuildTargets(index, patchCtx);

        return new MergeGroup { Category = category, TitleId = titleId, ContentIdSuffix = contentIdSuffix, PatchCtx = patchCtx, Index = index, Targets = targets, Owners = owners };
    }

    public static string BuildEntryPath(string prefix, MergeGroup group, string relativePath) =>
        group.Category == VitaContentCategory.Addcont ? NormalizeZipPath($"{prefix}/{group.TitleId}/{group.ContentIdSuffix}/{relativePath}") : NormalizeZipPath($"{prefix}/{group.TitleId}/{relativePath}");

    private static int GetEntrySortRank(VitaBatchSourceEntry entry)
    {
        var category = entry.Kind == VitaSourceKind.Pkg ? entry.Probe?.Category : entry.ItemCategory;

        return category switch
        {
            VitaContentCategory.App => 0,
            VitaContentCategory.Patch => 1,
            VitaContentCategory.Addcont => 2,
            _ => 3
        };
    }

    public static PatchContext GetPatchContext(Dictionary<string, PatchContext> cache, string patchPath, Action<string, LogLevel> log)
    {
        if (cache.TryGetValue(patchPath, out var existing))
            return existing;

        var accessor = VitaSourceAccessorFactory.Open(patchPath);
        var allPatchFiles = accessor.EnumerateAllFiles().ToList();
        var patchFiles = BuildPatchFileMap(allPatchFiles, log);
        var rawOverwriteFiles = allPatchFiles
            .Where(f => !PatchExtensions.Contains(Path.GetExtension(f)))
            .ToList();
        var ctx = new PatchContext { Accessor = accessor, PatchFiles = patchFiles, RawOverwriteFiles = rawOverwriteFiles };

        cache[patchPath] = ctx;

        return ctx;
    }

    private static Dictionary<string, string> BuildPatchFileMap(List<string> allPatchFiles, Action<string, LogLevel> log)
    {
        var groups = allPatchFiles
            .Where(f => PatchExtensions.Contains(Path.GetExtension(f)))
            .GroupBy(f => Path.GetFileNameWithoutExtension(f)!, StringComparer.OrdinalIgnoreCase);
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var group in groups)
        {
            var list = group.ToList();

            if (list.Count > 1)
                log($"패치 대상 '{group.Key}'에 대한 패치 파일이 {list.Count}개 중복됨: {string.Join(", ", list)} - 첫 번째({list[0]})만 사용함", LogLevel.Highlight);

            map[group.Key] = list[0];
        }

        return map;
    }

    public static string? ResolveWorkBinRel(IVitaSourceAccessor source, VitaSourceItem item, string? fallbackWorkBinRel)
    {
        string workBinRel = $"{item.SourcePath}/sce_sys/package/work.bin";

        if (source.FileExists(workBinRel))
            return workBinRel;

        if (fallbackWorkBinRel != null && source.FileExists(fallbackWorkBinRel))
            return fallbackWorkBinRel;

        return null;
    }

    public static string NormalizePath(string path) => path.Replace('\\', '/');

    public static string NormalizeZipPath(string path)
    {
        string normalized = NormalizePath(path);

        while (normalized.Contains("//"))
            normalized = normalized.Replace("//", "/");

        return normalized.Trim('/');
    }

    public static OwnerContext? BuildOwnerContext(VitaSourceItem item, string? fallbackWorkBinRel, Action<string, LogLevel> log)
    {
        var accessor = item.Accessor ?? throw new InvalidOperationException("소스 accessor가 없습니다.");
        string? workBinRel = ResolveWorkBinRel(accessor, item, fallbackWorkBinRel);

        if (workBinRel is null)
        {
            log($"{item.Category} {item.TitleId}: work.bin 없음, 건너뜀", LogLevel.Error);
            return null;
        }

        try
        {
            var license = WorkBinReader.Read(accessor, workBinRel);
            var table = VitaNoNpDrmDecryptor.ParseFileTable(accessor, item.SourcePath);

            return new OwnerContext { Item = item, Table = table, License = license, WorkBinRelativePath = workBinRel };
        }
        catch (Exception ex)
        {
            log($"{item.Category} {item.TitleId}: 파일 목록 확인 실패 - {ex.Message}", LogLevel.Error);
            return null;
        }
    }

    public static List<(string RelativePath, long Size)> GetAllOwnedFiles(OwnerContext owner)
    {
        var sizeByPath = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in owner.Table.Entries)
        {
            if (entry.Type.IsDirectory())
                continue;

            string relativePath = NormalizePath(entry.RelativePath ?? entry.Name);

            sizeByPath[relativePath] = entry.Size;
        }

        string prefix = string.IsNullOrEmpty(owner.Item.SourcePath) ? string.Empty : NormalizePath(owner.Item.SourcePath).TrimEnd('/') + "/";

        foreach (var rawPath in owner.Item.Accessor!.EnumerateAllFiles())
        {
            string normalized = NormalizePath(rawPath);

            if (prefix.Length > 0)
            {
                if (!normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    continue;

                normalized = normalized[prefix.Length..];
            }

            if (!sizeByPath.ContainsKey(normalized))
                sizeByPath[normalized] = owner.Item.Accessor!.GetFileSize(rawPath);
        }

        return [.. sizeByPath.Select(kv => (kv.Key, kv.Value))];
    }

    public static Dictionary<string, PatchAppEntry> BuildPatchAppIndex(OwnerContext? appOwner, OwnerContext? patchOwner)
    {
        var index = new Dictionary<string, PatchAppEntry>(StringComparer.OrdinalIgnoreCase);

        if (appOwner != null)
            AddOwnerToIndex(index, appOwner);

        if (patchOwner != null)
            AddOwnerToIndex(index, patchOwner);

        return index;
    }

    private static void AddOwnerToIndex(Dictionary<string, PatchAppEntry> index, OwnerContext owner)
    {
        for (int i = 0; i < owner.Table.Entries.Count; i++)
        {
            var entry = owner.Table.Entries[i];

            if (entry.Type.IsDirectory())
                continue;

            string relativePath = NormalizePath(entry.RelativePath ?? entry.Name);

            index[relativePath] = new PatchAppEntry { Owner = owner, EntryIndex = i };
        }
    }

    public static List<PatchTarget> BuildTargets(Dictionary<string, PatchAppEntry> patchAppIndex, PatchContext patchCtx)
    {
        var targets = new List<PatchTarget>();
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (relativePath, appEntry) in patchAppIndex)
        {
            string baseName = Path.GetFileName(relativePath);

            if (!patchCtx.PatchFiles.TryGetValue(baseName, out var patchFileRel))
                continue;

            targets.Add(new PatchTarget
            {
                RelativePath = relativePath,
                Kind = PatchTargetKind.Xdelta,
                Source = appEntry,
                PatchFileRel = patchFileRel,
                EstimatedSize = appEntry.FileEntry.Size
            });

            claimed.Add(relativePath);
        }

        foreach (var rawRel in patchCtx.RawOverwriteFiles)
        {
            string normalizedRawRel = NormalizePath(rawRel);

            if (claimed.Contains(normalizedRawRel))
                continue;

            if (!patchAppIndex.TryGetValue(normalizedRawRel, out var appEntry))
                continue;

            if (appEntry.Owner.Item.Category == VitaContentCategory.Addcont)
                continue;

            targets.Add(new PatchTarget
            {
                RelativePath = normalizedRawRel,
                Kind = PatchTargetKind.Raw,
                Source = appEntry,
                PatchFileRel = rawRel,
                EstimatedSize = patchCtx.Accessor.GetFileSize(rawRel)
            });

            claimed.Add(normalizedRawRel);
        }

        return targets;
    }

    public static async Task<byte[]> ResolveTargetBytesAsync(PatchTarget target, PatchContext patchCtx, Action<string, LogLevel> log, CancellationToken ct)
    {
        if (target.Kind == PatchTargetKind.Raw)
            return patchCtx.Accessor.ReadAllBytes(target.PatchFileRel);

        var appEntry = target.Source ?? throw new InvalidOperationException("xdelta 대상에 원본 정보가 없습니다.");
        var owner = appEntry.Owner;
        var entry = appEntry.FileEntry;
        byte[] sourceBytes = VitaNoNpDrmDecryptor.DecryptEntry(owner.Item.Accessor!, owner.Item.SourcePath, owner.License.Klicensee, entry, owner.Table.UnicvEntries[appEntry.EntryIndex], owner.Table.FilesSalt, out string? warning);

        if (warning != null)
            log($"{target.RelativePath}: {warning}", LogLevel.Highlight);

        byte[] patchBytes = patchCtx.Accessor.ReadAllBytes(target.PatchFileRel);

        return await UniversalPatcher.ApplyPatchAsync(sourceBytes, patchBytes, ct: ct);
    }

    public static async Task WriteEntryWithProgressAsync(ZipArchiveEntry zipEntry, byte[] data, long estimatedSize, ProgressReporter reporter, CancellationToken ct)
    {
        using var entryStream = zipEntry.Open();

        if (data.Length == 0)
        {
            reporter.AddProgress(estimatedSize);
            return;
        }

        int offset = 0;
        long reported = 0;

        while (offset < data.Length)
        {
            int size = Math.Min(ProgressChunkSize, data.Length - offset);

            await entryStream.WriteAsync(data.AsMemory(offset, size), ct);

            offset += size;

            long target = (long)((double)offset / data.Length * estimatedSize);

            if (target != reported)
                reporter.AddProgress(target - reported);

            reported = target;
        }
    }

    public static void WriteLicenseEntry(ZipArchive zip, HashSet<string> writtenEntries, OwnerContext owner)
    {
        if (!WorkBinReader.TryGetTitleIdFromContentId(owner.License.ContentId, out string licenseTitleId))
            return;

        string licenseEntryPath = NormalizeZipPath($"license/{licenseTitleId}/{owner.License.ContentId}.rif");

        if (!writtenEntries.Add(licenseEntryPath))
            return;

        byte[] workBinBytes = owner.Item.Accessor!.ReadAllBytes(owner.WorkBinRelativePath);
        var licenseEntry = zip.CreateEntry(licenseEntryPath, CompressionLevel.NoCompression);
        using var es = licenseEntry.Open();

        es.Write(workBinBytes);
    }

    public static string GetPatchedPrefix(VitaContentCategory category, VitaOutputTarget target) => (category, target) switch
    {
        (VitaContentCategory.App, VitaOutputTarget.Emu) => "app",
        (VitaContentCategory.Patch, VitaOutputTarget.Emu) => "app",
        (VitaContentCategory.Addcont, VitaOutputTarget.Emu) => "addcont",
        (VitaContentCategory.App, VitaOutputTarget.Retail) => "rePatch",
        (VitaContentCategory.Patch, VitaOutputTarget.Retail) => "rePatch",
        (VitaContentCategory.Addcont, VitaOutputTarget.Retail) => "reAddcont",
        _ => throw new NotSupportedException()
    };

    public static string GetBasePrefix(VitaContentCategory category) => category == VitaContentCategory.Addcont ? "addcont" : "app";

    public static string GetRetailBaseFolder(VitaContentCategory category) => category switch
    {
        VitaContentCategory.App => "app",
        VitaContentCategory.Patch => "patch",
        VitaContentCategory.Addcont => "addcont",
        _ => throw new NotSupportedException()
    };
}