using System.Buffers.Binary;
using System.Text;

namespace PsDoctor.Core.Observation;

/// <summary>
/// Ярлык Windows (<c>.lnk</c>, формат MS-SHLLINK) — ровно столько, сколько нужно слепку: куда ведёт и стоит ли галка
/// «Ярлык → Дополнительно → Запуск от имени администратора». Эта галка живёт в самом ярлыке, а не в реестре
/// (стенд, 02.10.2026).
/// </summary>
/// <param name="Target">Локальный путь цели из <c>LinkInfo</c>; <c>null</c> — его в ярлыке нет (сетевая цель, ярлык установщика).</param>
public sealed record ShellLink(string? Target, bool RunAsAdmin)
{
    private const int HeaderSize = 0x4C;
    private const uint HasLinkTargetIdList = 0x1;
    private const uint HasLinkInfo = 0x2;
    private const uint RunAsUser = 0x2000;
    private const uint VolumeIdAndLocalBasePath = 0x1;

    /// <summary><c>{00021401-0000-0000-C000-000000000046}</c> в порядке байтов файла.</summary>
    private static ReadOnlySpan<byte> LinkClsid => [0x01, 0x14, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0xC0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x46];

    /// <summary>
    /// Ярлык из байтов файла; не ярлык — <c>null</c>. Цель, которая не читается из оборванного или чужого <c>LinkInfo</c>,
    /// — <c>null</c>, флаг при этом известен: он в заголовке.
    /// </summary>
    public static ShellLink? Parse(ReadOnlySpan<byte> content)
    {
        if (content.Length < HeaderSize || BinaryPrimitives.ReadUInt32LittleEndian(content) != HeaderSize
            || !content.Slice(4, 16).SequenceEqual(LinkClsid))
        {
            return null;
        }
        var flags = BinaryPrimitives.ReadUInt32LittleEndian(content[0x14..]);
        return new ShellLink(ReadTarget(content, flags), (flags & RunAsUser) != 0);
    }

    private static string? ReadTarget(ReadOnlySpan<byte> content, uint flags)
    {
        var offset = HeaderSize;
        if ((flags & HasLinkTargetIdList) != 0)
        {
            if (content.Length < offset + 2)
            {
                return null;
            }
            offset += 2 + BinaryPrimitives.ReadUInt16LittleEndian(content[offset..]);
        }
        if ((flags & HasLinkInfo) == 0 || content.Length < offset + 28)
        {
            return null;
        }
        var info = content[offset..];
        var size = BinaryPrimitives.ReadUInt32LittleEndian(info);
        var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(info[4..]);
        var infoFlags = BinaryPrimitives.ReadUInt32LittleEndian(info[8..]);
        if (size > info.Length || (infoFlags & VolumeIdAndLocalBasePath) == 0)
        {
            return null;
        }
        info = info[..(int)size];
        // Заголовок от 0x24 несёт и Unicode-смещения; без них пути — в кодовой странице системы, а цель ProShow — ASCII.
        var unicode = headerSize >= 0x24 && info.Length >= 0x24;
        var basePath = unicode
            ? Utf16(info, BinaryPrimitives.ReadUInt32LittleEndian(info[28..]))
            : Ansi(info, BinaryPrimitives.ReadUInt32LittleEndian(info[16..]));
        var suffix = unicode
            ? Utf16(info, BinaryPrimitives.ReadUInt32LittleEndian(info[32..]))
            : Ansi(info, BinaryPrimitives.ReadUInt32LittleEndian(info[24..]));
        return basePath is null ? null : basePath + suffix;
    }

    private static string? Ansi(ReadOnlySpan<byte> info, uint offset)
    {
        if (offset == 0 || offset >= info.Length)
        {
            return null;
        }
        var text = info[(int)offset..];
        var end = text.IndexOf((byte)0);
        return end < 0 ? null : Encoding.Latin1.GetString(text[..end]);
    }

    private static string? Utf16(ReadOnlySpan<byte> info, uint offset)
    {
        if (offset == 0 || offset >= info.Length)
        {
            return null;
        }
        var text = info[(int)offset..];
        for (var i = 0; i + 1 < text.Length; i += 2)
        {
            if (text[i] == 0 && text[i + 1] == 0)
            {
                return Encoding.Unicode.GetString(text[..i]);
            }
        }
        return null;
    }
}
