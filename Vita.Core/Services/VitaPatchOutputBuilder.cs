using Common;
using System.IO.Compression;
using Vita.Core.Models;

namespace Vita.Core.Services;

public static class VitaPatchOutputBuilder
{
    public static Task<VitaMergeResult> BuildMergedFromEntriesAsync(List<VitaBatchSourceEntry> entries, string defaultPatchPath, string outputZipPath, VitaOutputTarget target, Action<string, LogLevel> log, IProgress<ProgressInfo>? progress = null, CancellationToken ct = default) =>
        VitaPatchShared.RunWithItemsAsync(entries, log, items => BuildCoreAsync(items, defaultPatchPath, outputZipPath, target, log, progress, ct));

    private static async Task<VitaMergeResult> BuildCoreAsync(List<VitaSourceItem> items, string defaultPatchPath, string outputZipPath, VitaOutputTarget target, Action<string, LogLevel> log, IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        var patchContexts = new Dictionary<string, PatchContext>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var groups = VitaPatchShared.BuildGroups(items, defaultPatchPath, patchContexts, log, ct);
            long totalBytes = groups.Sum(g => g.Targets.Sum(t => t.EstimatedSize) + GetWriteTotal(g, target));
            var reporter = new ProgressReporter("패치 적용 및 압축 중", string.Empty, totalBytes, progress);
            var writtenEntries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int patchCandidates = groups.Sum(g => g.Targets.Count);
            int patchedSuccess = 0;

            async Task<byte[]?> PatchAsync(MergeGroup group, PatchTarget patchTarget)
            {
                try
                {
                    byte[] bytes = await VitaPatchShared.ResolveTargetBytesAsync(patchTarget, group.PatchCtx, ct);

                    patchedSuccess++;

                    return bytes;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log($"[{group.Category}] {patchTarget.RelativePath}: 패치 실패, 원본 유지 - {ex.Message}", LogLevel.Error);

                    return null;
                }
                finally
                {
                    reporter.AddProgress(patchTarget.EstimatedSize);
                }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(outputZipPath)!);

            using (var zipStream = new FileStream(outputZipPath, FileMode.Create, FileAccess.Write))
            using (var zip = new ZipArchive(zipStream, ZipArchiveMode.Create))
            {
                foreach (var group in groups)
                {
                    ct.ThrowIfCancellationRequested();

                    var targetByPath = group.Targets.ToDictionary(t => t.RelativePath, StringComparer.OrdinalIgnoreCase);
                    var handledTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    if (target == VitaOutputTarget.Emu)
                    {
                        foreach (var (relativePath, appEntry) in group.Index)
                        {
                            ct.ThrowIfCancellationRequested();

                            byte[]? outputBytes = targetByPath.TryGetValue(relativePath, out var patchTarget) ? await PatchAsync(group, patchTarget) : null;

                            if (outputBytes is null)
                            {
                                try
                                {
                                    outputBytes = VitaNoNpDrmDecryptor.DecryptEntry(appEntry.Owner.Item.Accessor!, appEntry.Owner.Item.SourcePath, appEntry.Owner.License.Klicensee, appEntry.FileEntry, appEntry.Owner.Table.UnicvEntries[appEntry.EntryIndex], appEntry.Owner.Table.FilesSalt);
                                }
                                catch (Exception ex)
                                {
                                    log($"[{group.Category}] {relativePath}: 복호화 실패 - {ex.Message}", LogLevel.Error);
                                    continue;
                                }
                            }

                            string entryPath = VitaPatchShared.BuildEntryPath(VitaPatchShared.GetBasePrefix(group.Category), group, relativePath);

                            await WriteZipEntryAsync(zip, writtenEntries, entryPath, outputBytes, appEntry.FileEntry.Size, reporter, ct);
                        }
                    }
                    else
                    {
                        foreach (var owner in group.Owners)
                        {
                            foreach (var (relativePath, size) in VitaPatchShared.GetAllOwnedFiles(owner))
                            {
                                ct.ThrowIfCancellationRequested();

                                if (!await WriteOwnedFileAsync(zip, writtenEntries, owner, group, relativePath, size, reporter, log, ct))
                                    continue;

                                if (!targetByPath.TryGetValue(relativePath, out var patchTarget))
                                    continue;

                                if (!handledTargets.Add(relativePath))
                                    continue;

                                byte[]? patchedBytes = await PatchAsync(group, patchTarget);

                                if (patchedBytes != null)
                                {
                                    string patchedEntryPath = VitaPatchShared.BuildEntryPath(VitaPatchShared.GetPatchedPrefix(group.Category, target), group, relativePath);

                                    await WriteZipEntryAsync(zip, writtenEntries, patchedEntryPath, patchedBytes, patchTarget.EstimatedSize, reporter, ct);
                                }
                            }
                        }
                    }

                    if (target == VitaOutputTarget.Emu)
                    {
                        foreach (var owner in group.Owners)
                            VitaPatchShared.WriteLicenseEntry(zip, writtenEntries, owner);
                    }
                }

                reporter.ForceReport();
            }

            return new VitaMergeResult { TotalFiles = writtenEntries.Count, PatchCandidates = patchCandidates, PatchedSuccessfully = patchedSuccess };
        }
        finally
        {
            foreach (var patchCtx in patchContexts.Values)
                patchCtx.Accessor.Dispose();
        }
    }

    private static long GetWriteTotal(MergeGroup group, VitaOutputTarget target)
    {
        if (target == VitaOutputTarget.Emu)
            return group.Index.Values.Sum(e => (long)e.FileEntry.Size);

        long ownedTotal = group.Owners.Sum(owner => VitaPatchShared.GetAllOwnedFiles(owner).Sum(f => f.Size));

        return ownedTotal + group.Targets.Sum(t => t.EstimatedSize);
    }

    private static async Task<bool> WriteOwnedFileAsync(ZipArchive zip, HashSet<string> writtenEntries, OwnerContext owner, MergeGroup group, string relativePath, long size, ProgressReporter reporter, Action<string, LogLevel> log, CancellationToken ct)
    {
        string srcRel = $"{owner.Item.SourcePath}/{relativePath}";
        byte[] rawBytes;

        try
        {
            rawBytes = owner.Item.Accessor!.ReadAllBytes(srcRel);
        }
        catch (Exception ex)
        {
            log($"[{owner.Item.Category}] {relativePath}: 원본 읽기 실패 - {ex.Message}", LogLevel.Error);

            return false;
        }

        string baseEntryPath = VitaPatchShared.BuildEntryPath(VitaPatchShared.GetRetailBaseFolder(owner.Item.Category), group, relativePath);

        await WriteZipEntryAsync(zip, writtenEntries, baseEntryPath, rawBytes, size, reporter, ct);

        return true;
    }

    private static async Task WriteZipEntryAsync(ZipArchive zip, HashSet<string> writtenEntries, string entryPath, byte[] data, long estimatedSize, ProgressReporter reporter, CancellationToken ct)
    {
        if (!writtenEntries.Add(entryPath))
            return;

        var zipEntry = zip.CreateEntry(entryPath, CompressionLevel.NoCompression);

        await VitaPatchShared.WriteEntryWithProgressAsync(zipEntry, data, estimatedSize, reporter, ct);
    }
}