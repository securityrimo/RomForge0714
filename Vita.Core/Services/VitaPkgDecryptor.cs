using System.Buffers.Binary;
using System.Text;
using Vita.Core.Cryptography;
using Vita.Core.Models;

namespace Vita.Core.Services;

public static class VitaPkgDecryptor
{
    private const uint PkgMagic = 0x7f504b47;
    private const uint ExtMagic = 0x7f657874;

    public static VitaPkgHeader ReadHeader(Stream pkgStream)
    {
        pkgStream.Seek(0, SeekOrigin.Begin);

        var header = new byte[VitaPkgHeader.HeaderSize + VitaPkgHeader.HeaderExtSize];

        pkgStream.ReadExactly(header);

        if (BinaryPrimitives.ReadUInt32BigEndian(header) != PkgMagic || BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(VitaPkgHeader.HeaderSize)) != ExtMagic)
            throw new InvalidDataException("PKG 파일이 아닙니다.");

        long metaOffset = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(8));
        int metaCount = (int)BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(12));
        int itemCount = (int)BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(20));
        long encOffset = (long)BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(32));
        long encSize = (long)BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(40));
        byte[] iv = header.AsSpan(0x70, 16).ToArray();
        int keyType = header[0xe7] & 7;
        uint contentType = 0;
        long itemsOffset = 0;
        long itemsSize = 0;
        long cursor = metaOffset;

        for (int i = 0; i < metaCount; i++)
        {
            pkgStream.Seek(cursor, SeekOrigin.Begin);

            var block = new byte[16];

            pkgStream.ReadExactly(block);

            uint type = BinaryPrimitives.ReadUInt32BigEndian(block.AsSpan(0));
            uint size = BinaryPrimitives.ReadUInt32BigEndian(block.AsSpan(4));

            if (type == 2)
                contentType = BinaryPrimitives.ReadUInt32BigEndian(block.AsSpan(8));
            else if (type == 13)
            {
                itemsOffset = BinaryPrimitives.ReadUInt32BigEndian(block.AsSpan(8));
                itemsSize = BinaryPrimitives.ReadUInt32BigEndian(block.AsSpan(12));
            }

            cursor += 8 + size;
        }

        if (contentType is not ((uint)VitaContentType.App or (uint)VitaContentType.Dlc or (uint)VitaContentType.Psm or (uint)VitaContentType.PsmUnk))
            throw new NotSupportedException($"지원하지 않는 PKG content type: 0x{contentType:x}");

        string contentId = Encoding.ASCII
            .GetString(header, VitaPkgHeader.ContentIdOffset, VitaPkgHeader.ContentIdSize)
            .TrimEnd('\0');

        return new VitaPkgHeader
        {
            ItemCount = itemCount,
            EncOffset = encOffset,
            EncSize = encSize,
            Iv = iv,
            KeyType = keyType,
            ContentType = contentType,
            ItemsOffset = itemsOffset,
            ItemsSize = itemsSize,
            ContentId = contentId
        };
    }

    public static VitaAes128Ctr CreateCipher(VitaPkgHeader header)
    {
        byte[] mainKey = VitaPkgKeys.DeriveMainKey(header.KeyType, header.Iv);

        return new VitaAes128Ctr(mainKey, header.Iv);
    }

    public static List<VitaPkgItem> ReadItemTable(Stream pkgStream, VitaPkgHeader header, VitaAes128Ctr ctr)
    {
        var items = new List<VitaPkgItem>(header.ItemCount);

        for (int i = 0; i < header.ItemCount; i++)
        {
            long itemOffset = header.ItemsOffset + i * 32;
            var item = new byte[32];

            pkgStream.Seek(header.EncOffset + itemOffset, SeekOrigin.Begin);
            pkgStream.ReadExactly(item);
            ctr.XorAt(itemOffset / 16, item);

            uint nameOffset = BinaryPrimitives.ReadUInt32BigEndian(item.AsSpan(0));
            uint nameSize = BinaryPrimitives.ReadUInt32BigEndian(item.AsSpan(4));
            long dataOffset = (long)BinaryPrimitives.ReadUInt64BigEndian(item.AsSpan(8));
            long dataSize = (long)BinaryPrimitives.ReadUInt64BigEndian(item.AsSpan(16));
            byte flags = item[27];
            var nameBytes = new byte[nameSize];

            pkgStream.Seek(header.EncOffset + nameOffset, SeekOrigin.Begin);
            pkgStream.ReadExactly(nameBytes);
            ctr.XorAt(nameOffset / 16, nameBytes);

            string name = Encoding.UTF8.GetString(nameBytes);

            items.Add(new VitaPkgItem
            {
                Name = name,
                DataOffset = dataOffset,
                DataSize = dataSize,
                Flags = flags
            });
        }

        return items;
    }

    public static bool IsDirectory(VitaPkgItem item) => item.Flags is 4 or 18;

    public static byte[] DecryptItemData(Stream pkgStream, VitaPkgHeader header, VitaAes128Ctr ctr, VitaPkgItem item)
    {
        var data = new byte[item.DataSize];

        pkgStream.Seek(header.EncOffset + item.DataOffset, SeekOrigin.Begin);
        pkgStream.ReadExactly(data);
        ctr.XorAt(item.DataOffset / 16, data);

        return data;
    }
}