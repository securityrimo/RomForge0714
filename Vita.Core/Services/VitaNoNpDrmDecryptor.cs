using Vita.Core.Cryptography;
using Vita.Core.Models;

namespace Vita.Core.Services;

public static class VitaNoNpDrmDecryptor
{
    public static VitaPfsFileTable ParseFileTable(IVitaSourceAccessor accessor, string titleRelPath)
    {
        string filesDbRel = Combine(titleRelPath, "sce_pfs/files.db");
        string unicvDbRel = Combine(titleRelPath, "sce_pfs/unicv.db");

        if (!accessor.FileExists(filesDbRel))
            throw new FileNotFoundException("files.db를 찾을 수 없습니다.", filesDbRel);

        if (!accessor.FileExists(unicvDbRel))
            throw new FileNotFoundException("unicv.db를 찾을 수 없습니다.", unicvDbRel);

        using var filesDbStream = new MemoryStream(accessor.ReadAllBytes(filesDbRel));
        var flat = PfsFilesDbParser.Parse(filesDbStream, out uint filesSalt);
        using var unicvDbStream = new MemoryStream(accessor.ReadAllBytes(unicvDbRel));
        var unicv = PfsUnicvDbParser.Parse(unicvDbStream, flat.Count);

        return new VitaPfsFileTable { Entries = flat, UnicvEntries = unicv, FilesSalt = filesSalt };
    }

    public static byte[] DecryptEntry(IVitaSourceAccessor accessor, string titleRelPath, byte[] klicensee, PfsFlatEntry entry, PfsUnicvEntry unicvEntry, uint filesSalt, out string? warning)
    {
        warning = null;

        string relativePath = entry.RelativePath ?? entry.Name;
        string srcRel = Combine(titleRelPath, relativePath);
        byte[] data = accessor.ReadAllBytes(srcRel);

        if (!entry.Type.IsEncrypted())
            return data;

        if (unicvEntry.NSectors > 0 && data.Length > 0)
        {
            var f00d = new VitaF00DEmulator();

            if (unicvEntry.HasDbSeed)
            {
                var cipher = new VitaPfsGameDataCipher(f00d, klicensee, unicvEntry.DbSeed, (int)unicvEntry.FileSectorSize);

                cipher.DecryptRange(0, data);
            }
            else if (unicvEntry.TableMagic == "SCEIFTBL")
            {
                byte[] tweakEncKey = VitaPfsLegacyKeyDerivation.ComputeTweakEncKey(filesSalt, (uint)unicvEntry.PageNumber);
                var cipher = VitaPfsGameDataCipher.FromPrecomputedTweakKey(f00d, klicensee, tweakEncKey, (int)unicvEntry.FileSectorSize);

                cipher.DecryptRange(0, data);
            }
        }

        return data;
    }

    private static string Combine(string basePath, string relative) => string.IsNullOrEmpty(basePath) ? relative : $"{basePath.TrimEnd('/')}/{relative.Replace('\\', '/')}";
}