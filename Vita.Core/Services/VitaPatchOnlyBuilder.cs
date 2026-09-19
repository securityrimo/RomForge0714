using Common;
using System.IO.Compression;
using Vita.Core.Models;

namespace Vita.Core.Services;

public static class VitaPatchOnlyBuilder
{
    public static async Task<VitaPatchOnlyResult> BuildFromEntriesAsync(List<VitaBatchSourceEntry> entries, string defaultPatchPath, string outputZipPath, VitaOutputTarget target, Action<string, LogLevel> log, IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        var (items, ownedAccessors) = VitaPatchShared.LoadItems(entries, log);

        try
        {
            return await BuildCoreAsync(items, defaultPatchPath, outputZipPath, target, log, progress, ct);
        }
        finally
        {
            foreach (var accessor in ownedAccessors)
                accessor.Dispose();
        }
    }

    private static async Task<VitaPatchOnlyResult> BuildCoreAsync(List<VitaSourceItem> items, string defaultPatchPath, string outputZipPath, VitaOutputTarget target, Action<string, LogLevel> log, IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        var patchContexts = new Dictionary<string, PatchContext>(StringComparer.OrdinalIgnoreCase);
        var appByTitle = items.Where(i => i.Category == VitaContentCategory.App).ToDictionary(i => i.TitleId, StringComparer.OrdinalIgnoreCase);
        var patchByTitle = items.Where(i => i.Category == VitaContentCategory.Patch).ToDictionary(i => i.TitleId, StringComparer.OrdinalIgnoreCase);
        var addcontItems = items.Where(i => i.Category == VitaContentCategory.Addcont).ToList();
        var titleIds = appByTitle.Keys.Union(patchByTitle.Keys, StringComparer.OrdinalIgnoreCase).ToList();
        var groups = new List<ProcessGroup>();

        foreach (var titleId in titleIds)
        {
            ct.ThrowIfCancellationRequested();

            appByTitle.TryGetValue(titleId, out var appItem);
            patchByTitle.TryGetValue(titleId, out var patchItem);

            string? appWorkBinFallback = appItem != null ? $"{appItem.SourcePath}/sce_sys/package/work.bin" : null;
            var appOwner = appItem != null ? VitaPatchShared.BuildOwnerContext(appItem, null, log) : null;
            var patchOwner = patchItem != null ? VitaPatchShared.BuildOwnerContext(patchItem, appWorkBinFallback, log) : null;
            var owners = new List<OwnerContext>();

            if (appOwner != null)
                owners.Add(appOwner);

            if (patchOwner != null)
                owners.Add(patchOwner);

            if (owners.Count == 0)
                continue;

            string patchPackagePath = (patchItem?.PatchPathOverride ?? appItem?.PatchPathOverride) ?? defaultPatchPath;
            var patchCtx = VitaPatchShared.GetPatchContext(patchContexts, patchPackagePath, log);
            var patchAppIndex = VitaPatchShared.BuildPatchAppIndex(appOwner, patchOwner);
            var targets = VitaPatchShared.BuildTargets(patchAppIndex, patchCtx);

            groups.Add(new ProcessGroup { Category = VitaContentCategory.App, TitleId = titleId, PatchCtx = patchCtx, Targets = targets, Owners = owners });
        }

        foreach (var addcontItem in addcontItems)
        {
            ct.ThrowIfCancellationRequested();

            var owner = VitaPatchShared.BuildOwnerContext(addcontItem, null, log);

            if (owner is null)
                continue;

            string patchPackagePath = addcontItem.PatchPathOverride ?? defaultPatchPath;
            var patchCtx = VitaPatchShared.GetPatchContext(patchContexts, patchPackagePath, log);
            var index = VitaPatchShared.BuildPatchAppIndex(owner, null);
            var targets = VitaPatchShared.BuildTargets(index, patchCtx);

            groups.Add(new ProcessGroup { Category = VitaContentCategory.Addcont, TitleId = addcontItem.TitleId, ContentIdSuffix = addcontItem.ContentIdSuffix, PatchCtx = patchCtx, Targets = targets, Owners = [owner] });
        }

        long totalBytes = groups.Sum(g => g.Targets.Sum(t => t.EstimatedSize));
        var reporter = new ProgressReporter("패치 적용 중", string.Empty, totalBytes, progress);
        var writtenEntries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int matched = 0;
        int success = 0;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outputZipPath)!);

            using var zipStream = new FileStream(outputZipPath, FileMode.Create, FileAccess.Write);
            using var zip = new ZipArchive(zipStream, ZipArchiveMode.Create);

            foreach (var group in groups)
            {
                ct.ThrowIfCancellationRequested();

                int groupMatched = 0;
                int groupSuccess = 0;

                foreach (var t in group.Targets)
                {
                    ct.ThrowIfCancellationRequested();

                    groupMatched++;

                    string prefix = VitaPatchShared.GetPatchedPrefix(group.Category, target);
                    string entryPath = group.Category == VitaContentCategory.Addcont ? VitaPatchShared.NormalizeZipPath($"{prefix}/{group.TitleId}/{group.ContentIdSuffix}/{t.RelativePath}") : VitaPatchShared.NormalizeZipPath($"{prefix}/{group.TitleId}/{t.RelativePath}");

                    if (!writtenEntries.Add(entryPath))
                    {
                        log($"[{group.Category}] {t.RelativePath}: 이미 같은 경로로 추가된 항목이라 건너뜀 (중복)", LogLevel.Highlight);
                        continue;
                    }

                    try
                    {
                        byte[] outputBytes = await VitaPatchShared.ResolveTargetBytesAsync(t, group.PatchCtx, log, ct);
                        var zipEntry = zip.CreateEntry(entryPath, CompressionLevel.NoCompression);

                        await VitaPatchShared.WriteEntryWithProgressAsync(zipEntry, outputBytes, t.EstimatedSize, reporter, ct);

                        groupSuccess++;
                    }
                    catch (Exception ex)
                    {
                        log($"[{group.Category}] {t.RelativePath}: 처리 실패 - {ex.Message}", LogLevel.Error);
                        reporter.AddProgress(t.EstimatedSize);
                    }
                }

                matched += groupMatched;
                success += groupSuccess;

                LogGroupResult(log, group, groupMatched, groupSuccess);

                if (target == VitaOutputTarget.Emu)
                {
                    foreach (var owner in group.Owners)
                        VitaPatchShared.WriteLicenseEntry(zip, writtenEntries, owner);
                }
            }

            reporter.ForceReport();

            return new VitaPatchOnlyResult { MatchedCandidates = matched, PatchedSuccessfully = success };
        }
        finally
        {
            foreach (var patchCtx in patchContexts.Values)
                patchCtx.Accessor.Dispose();
        }
    }

    private static void LogGroupResult(Action<string, LogLevel> log, ProcessGroup group, int matched, int success)
    {
        if (matched == 0)
            return;

        string name = group.Category == VitaContentCategory.Addcont ? $"{group.TitleId}/{group.ContentIdSuffix}" : group.TitleId;

        if (success == matched)
            log($"[{group.Category}] {name}: {matched}개 중 {success}개 패치 완료", LogLevel.Ok);
        else
            log($"[{group.Category}] {name}: {matched}개 중 {success}개만 패치 성공 ({matched - success}개 실패)", LogLevel.Error);
    }
}