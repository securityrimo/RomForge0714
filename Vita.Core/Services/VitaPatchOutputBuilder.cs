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

            long patchTotal = groups.Sum(g => g.Targets.Sum(t => t.EstimatedSize));
            var patchReporter = new ProgressReporter("패치 적용 중", string.Empty, patchTotal, progress);
            var resolved = new Dictionary<(MergeGroup Group, string RelativePath), ResolvedTarget>();
            int patchCandidates = 0;
            int patchedSuccess = 0;

            foreach (var group in groups)
            {
                ct.ThrowIfCancellationRequested();

                foreach (var t in group.Targets)
                {
                    ct.ThrowIfCancellationRequested();

                    patchCandidates++;

                    try
                    {
                        byte[] bytes = await VitaPatchShared.ResolveTargetBytesAsync(t, group.PatchCtx, log, ct);

                        resolved[(group, t.RelativePath)] = new ResolvedTarget(bytes, t.EstimatedSize);
                        patchedSuccess++;
                    }
                    catch (Exception ex)
                    {
                        log($"[{group.Category}] {t.RelativePath}: 패치 실패, 원본 유지 - {ex.Message}", LogLevel.Error);
                    }

                    patchReporter.AddProgress(t.EstimatedSize);
                }
            }

            patchReporter.ForceReport();

            long zipTotal = 0;

            foreach (var group in groups)
            {
                if (target == VitaOutputTarget.Emu)
                {
                    foreach (var appEntry in group.Index.Values)
                        zipTotal += appEntry.FileEntry.Size;
                }
                else
                {
                    foreach (var owner in group.Owners)
                    {
                        foreach (var (_, size) in VitaPatchShared.GetAllOwnedFiles(owner))
                            zipTotal += size;
                    }

                    foreach (var t in group.Targets)
                    {
                        if (resolved.ContainsKey((group, t.RelativePath)))
                            zipTotal += t.EstimatedSize;
                    }
                }
            }

            var zipReporter = new ProgressReporter("압축 중", string.Empty, zipTotal, progress);
            var writtenEntries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            Directory.CreateDirectory(Path.GetDirectoryName(outputZipPath)!);

            using (var zipStream = new FileStream(outputZipPath, FileMode.Create, FileAccess.Write))
            using (var zip = new ZipArchive(zipStream, ZipArchiveMode.Create))
            {
                foreach (var group in groups)
                {
                    ct.ThrowIfCancellationRequested();

                    if (target == VitaOutputTarget.Emu)
                    {
                        foreach (var (relativePath, appEntry) in group.Index)
                        {
                            ct.ThrowIfCancellationRequested();

                            bool isTarget = resolved.TryGetValue((group, relativePath), out var resolvedTarget);
                            byte[] outputBytes;

                            if (isTarget)
                                outputBytes = resolvedTarget!.Bytes;
                            else
                            {
                                try
                                {
                                    outputBytes = VitaNoNpDrmDecryptor.DecryptEntry(appEntry.Owner.Item.Accessor!, appEntry.Owner.Item.SourcePath, appEntry.Owner.License.Klicensee, appEntry.FileEntry, appEntry.Owner.Table.UnicvEntries[appEntry.EntryIndex], appEntry.Owner.Table.FilesSalt, out string? warning);

                                    if (warning != null)
                                        log($"[{group.Category}] {warning}", LogLevel.Highlight);
                                }
                                catch (Exception ex)
                                {
                                    log($"[{group.Category}] {relativePath}: 복호화 실패 - {ex.Message}", LogLevel.Error);
                                    continue;
                                }
                            }

                            string entryPath = VitaPatchShared.BuildEntryPath(VitaPatchShared.GetBasePrefix(group.Category), group, relativePath);

                            await WriteZipEntryAsync(zip, writtenEntries, entryPath, outputBytes, appEntry.FileEntry.Size, zipReporter, log, group.Category, relativePath, ct);
                        }
                    }
                    else
                    {
                        foreach (var owner in group.Owners)
                        {
                            foreach (var (relativePath, size) in VitaPatchShared.GetAllOwnedFiles(owner))
                            {
                                ct.ThrowIfCancellationRequested();

                                string srcRel = $"{owner.Item.SourcePath}/{relativePath}";
                                byte[] rawBytes;

                                try
                                {
                                    rawBytes = owner.Item.Accessor!.ReadAllBytes(srcRel);
                                }
                                catch (Exception ex)
                                {
                                    log($"[{owner.Item.Category}] {relativePath}: 원본 읽기 실패 - {ex.Message}", LogLevel.Error);
                                    continue;
                                }

                                string baseEntryPath = VitaPatchShared.BuildEntryPath(VitaPatchShared.GetRetailBaseFolder(owner.Item.Category), group, relativePath);

                                await WriteZipEntryAsync(zip, writtenEntries, baseEntryPath, rawBytes, size, zipReporter, log, owner.Item.Category, relativePath, ct);

                                if (resolved.TryGetValue((group, relativePath), out var resolvedTarget))
                                {
                                    string patchedEntryPath = VitaPatchShared.BuildEntryPath(VitaPatchShared.GetPatchedPrefix(group.Category, target), group, relativePath);

                                    await WriteZipEntryAsync(zip, writtenEntries, patchedEntryPath, resolvedTarget.Bytes, resolvedTarget.EstimatedSize, zipReporter, log, group.Category, relativePath, ct);
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

                zipReporter.ForceReport();
            }

            return new VitaMergeResult { TotalFiles = writtenEntries.Count, PatchCandidates = patchCandidates, PatchedSuccessfully = patchedSuccess };
        }
        finally
        {
            foreach (var patchCtx in patchContexts.Values)
                patchCtx.Accessor.Dispose();
        }
    }

    private static async Task WriteZipEntryAsync(ZipArchive zip, HashSet<string> writtenEntries, string entryPath, byte[] data, long estimatedSize, ProgressReporter reporter, Action<string, LogLevel> log, VitaContentCategory category, string relativePath, CancellationToken ct)
    {
        if (!writtenEntries.Add(entryPath))
        {
            log($"[{category}] {relativePath}: 이미 같은 경로로 추가된 항목이라 건너뜀 (중복)", LogLevel.Highlight);
            return;
        }

        var zipEntry = zip.CreateEntry(entryPath, CompressionLevel.NoCompression);

        await VitaPatchShared.WriteEntryWithProgressAsync(zipEntry, data, estimatedSize, reporter, ct);
    }
}