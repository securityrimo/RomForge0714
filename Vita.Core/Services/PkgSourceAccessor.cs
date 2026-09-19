using Vita.Core.Cryptography;
using Vita.Core.Models;

namespace Vita.Core.Services;

public sealed class PkgSourceAccessor : IVitaSourceAccessor
{
    private const string WorkBinRelativePath = "sce_sys/package/work.bin";
    private const string HeadBinRelativePath = "sce_sys/package/head.bin";
    private const string TailBinRelativePath = "sce_sys/package/tail.bin";
    private const string StatBinRelativePath = "sce_sys/package/stat.bin";
    private const string BodyBinRelativePath = "sce_sys/package/body.bin";
    private const string DigsBinRelativePath = "sce_sys/package/digs.bin";
    private const int StatBinSize = 768;

    private readonly FileStream _stream;
    private readonly VitaAes128Ctr _ctr;
    private readonly Dictionary<string, VitaPkgItem> _items;
    private readonly byte[]? _workBinBytes;
    private readonly byte[] _headBinBytes;
    private readonly byte[] _tailBinBytes;
    private readonly VitaPkgItem? _bodyBinItem;

    public VitaPkgHeader Header { get; }

    public PkgSourceAccessor(string pkgPath, string? license = null)
        : this(pkgPath, license, null)
    {
    }

    public PkgSourceAccessor(string pkgPath, byte[] klicensee)
        : this(pkgPath, null, klicensee)
    {
    }

    private PkgSourceAccessor(string pkgPath, string? license, byte[]? klicensee)
    {
        _stream = new FileStream(pkgPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        Header = VitaPkgDecryptor.ReadHeader(_stream);
        _ctr = VitaPkgDecryptor.CreateCipher(Header);

        var items = VitaPkgDecryptor.ReadItemTable(_stream, Header, _ctr);

        _items = items
            .Where(i => !VitaPkgDecryptor.IsDirectory(i))
            .GroupBy(i => i.Name.Trim('/'), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        if (_items.TryGetValue(DigsBinRelativePath, out var digsItem))
        {
            _bodyBinItem = digsItem;
            _items.Remove(DigsBinRelativePath);
        }

        _headBinBytes = ReadRawRange(0, Header.EncOffset + Header.ItemsSize);

        long tailStart = Header.EncOffset + Header.EncSize;

        _tailBinBytes = ReadRawRange(tailStart, _stream.Length - tailStart);

        byte[]? resolvedKlicensee = klicensee ?? (license != null ? VitaPkgLicenseResolver.ResolveKlicensee(license, Header.ContentId) : null);

        if (resolvedKlicensee != null)
            _workBinBytes = VitaPkgLicenseResolver.BuildWorkBin(Header.ContentId, resolvedKlicensee);
    }

    private byte[] ReadRawRange(long offset, long size)
    {
        var buffer = new byte[size];

        _stream.Seek(offset, SeekOrigin.Begin);
        _stream.ReadExactly(buffer);

        return buffer;
    }

    public bool DirectoryExists(string relativePath)
    {
        string prefix = Normalize(relativePath);

        if (prefix.Length == 0)
            return true;

        if (prefix.Equals("sce_sys/package", StringComparison.OrdinalIgnoreCase) || prefix.Equals("sce_sys", StringComparison.OrdinalIgnoreCase))
            return true;

        prefix += "/";

        return _items.Keys.Any(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    public IEnumerable<string> EnumerateDirectoryNames(string relativePath)
    {
        string prefix = Normalize(relativePath);

        if (prefix.Length > 0)
            prefix += "/";

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var key in _items.Keys)
        {
            if (!key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;

            string rest = key[prefix.Length..];
            int slash = rest.IndexOf('/');

            if (slash > 0)
                names.Add(rest[..slash]);
        }

        return names;
    }

    public IEnumerable<string> EnumerateAllFiles()
    {
        var virtualFiles = new List<string> { HeadBinRelativePath, TailBinRelativePath, StatBinRelativePath };

        if (_bodyBinItem != null)
            virtualFiles.Add(BodyBinRelativePath);

        if (_workBinBytes != null)
            virtualFiles.Add(WorkBinRelativePath);

        return _items.Keys.Concat(virtualFiles);
    }

    public bool FileExists(string relativePath)
    {
        string rel = Normalize(relativePath);

        if (rel.Equals(WorkBinRelativePath, StringComparison.OrdinalIgnoreCase))
            return _workBinBytes != null;

        if (rel.Equals(HeadBinRelativePath, StringComparison.OrdinalIgnoreCase) || rel.Equals(TailBinRelativePath, StringComparison.OrdinalIgnoreCase) || rel.Equals(StatBinRelativePath, StringComparison.OrdinalIgnoreCase))
            return true;

        if (rel.Equals(BodyBinRelativePath, StringComparison.OrdinalIgnoreCase))
            return _bodyBinItem != null;

        return _items.ContainsKey(rel);
    }

    public byte[] ReadAllBytes(string relativePath)
    {
        string rel = Normalize(relativePath);

        if (rel.Equals(WorkBinRelativePath, StringComparison.OrdinalIgnoreCase))
            return _workBinBytes ?? throw new InvalidOperationException("이 PkgSourceAccessor는 라이선스 없이 열려서 work.bin을 제공할 수 없습니다.");

        if (rel.Equals(HeadBinRelativePath, StringComparison.OrdinalIgnoreCase))
            return _headBinBytes;

        if (rel.Equals(TailBinRelativePath, StringComparison.OrdinalIgnoreCase))
            return _tailBinBytes;

        if (rel.Equals(StatBinRelativePath, StringComparison.OrdinalIgnoreCase))
            return new byte[StatBinSize];

        if (rel.Equals(BodyBinRelativePath, StringComparison.OrdinalIgnoreCase))
        {
            if (_bodyBinItem is null)
                throw new FileNotFoundException(relativePath);

            return ReadRawRange(Header.EncOffset + _bodyBinItem.DataOffset, _bodyBinItem.DataSize);
        }

        if (!_items.TryGetValue(rel, out var item))
            throw new FileNotFoundException(relativePath);

        return VitaPkgDecryptor.DecryptItemData(_stream, Header, _ctr, item);
    }

    public long GetFileSize(string relativePath)
    {
        string rel = Normalize(relativePath);

        if (rel.Equals(WorkBinRelativePath, StringComparison.OrdinalIgnoreCase))
            return _workBinBytes?.Length ?? 0;

        if (rel.Equals(HeadBinRelativePath, StringComparison.OrdinalIgnoreCase))
            return _headBinBytes.Length;

        if (rel.Equals(TailBinRelativePath, StringComparison.OrdinalIgnoreCase))
            return _tailBinBytes.Length;

        if (rel.Equals(StatBinRelativePath, StringComparison.OrdinalIgnoreCase))
            return StatBinSize;

        if (rel.Equals(BodyBinRelativePath, StringComparison.OrdinalIgnoreCase))
            return _bodyBinItem?.DataSize ?? 0;

        return ReadAllBytes(relativePath).Length;
    }

    private static string Normalize(string path) => VitaPatchShared.NormalizeZipPath(path);

    public void Dispose()
    {
        _ctr.Dispose();
        _stream.Dispose();
    }
}