using System.Buffers.Binary;
using System.Text;
using PsDoctor.Core.Observation;
using Xunit;

namespace PsDoctor.Core.Tests;

/// <summary>Разбор ярлыка на байтах, собранных по MS-SHLLINK: заголовок, список ID оболочки, LinkInfo.</summary>
public sealed class ShellLinkTests
{
    private const string ProShow = @"C:\Program Files (x86)\Photodex\ProShow Producer\proshow.exe";
    private const uint СписокId = 0x1;
    private const uint ЕстьLinkInfo = 0x2;
    private const uint ОтАдминистратора = 0x2000;

    private static byte[] Ярлык(uint флаги, string цель = ProShow, bool юникод = false, string суффикс = "")
    {
        var файл = new List<byte>();
        var заголовок = new byte[0x4C];
        BinaryPrimitives.WriteUInt32LittleEndian(заголовок, 0x4C);
        new Guid("00021401-0000-0000-C000-000000000046").TryWriteBytes(заголовок.AsSpan(4));
        BinaryPrimitives.WriteUInt32LittleEndian(заголовок.AsSpan(0x14), флаги);
        файл.AddRange(заголовок);
        if ((флаги & СписокId) != 0)
        {
            файл.AddRange([10, 0]);
            файл.AddRange(new byte[10]);
        }
        if ((флаги & ЕстьLinkInfo) != 0)
        {
            файл.AddRange(LinkInfo(цель, суффикс, юникод));
        }
        файл.AddRange([0, 0, 0, 0]);
        return [.. файл];
    }

    /// <summary>Шапка, заглушка VolumeID, путь и суффикс в ANSI, при <paramref name="юникод"/> — ещё и в UTF-16.</summary>
    private static byte[] LinkInfo(string цель, string суффикс, bool юникод)
    {
        var шапка = юникод ? 0x24 : 0x1C;
        var том = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(том, 16);
        // В ANSI-части — заведомо не та цель: тест видит, что при Unicode-смещениях берётся Unicode.
        var ansi = Encoding.ASCII.GetBytes((юникод ? "C:\\ansi.exe" : цель) + "\0");
        var ansiСуффикс = Encoding.ASCII.GetBytes(суффикс + "\0");
        var uni = Encoding.Unicode.GetBytes(цель + "\0");
        var uniСуффикс = Encoding.Unicode.GetBytes(суффикс + "\0");
        var томС = шапка;
        var путьС = томС + том.Length;
        var суффиксС = путьС + ansi.Length;
        var uniС = суффиксС + ansiСуффикс.Length;
        var uniСуффиксС = uniС + uni.Length;
        var размер = юникод ? uniСуффиксС + uniСуффикс.Length : uniС;
        var блок = new byte[размер];
        BinaryPrimitives.WriteUInt32LittleEndian(блок.AsSpan(0), (uint)размер);
        BinaryPrimitives.WriteUInt32LittleEndian(блок.AsSpan(4), (uint)шапка);
        BinaryPrimitives.WriteUInt32LittleEndian(блок.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(блок.AsSpan(12), (uint)томС);
        BinaryPrimitives.WriteUInt32LittleEndian(блок.AsSpan(16), (uint)путьС);
        BinaryPrimitives.WriteUInt32LittleEndian(блок.AsSpan(24), (uint)суффиксС);
        if (юникод)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(блок.AsSpan(28), (uint)uniС);
            BinaryPrimitives.WriteUInt32LittleEndian(блок.AsSpan(32), (uint)uniСуффиксС);
            uni.CopyTo(блок, uniС);
            uniСуффикс.CopyTo(блок, uniСуффиксС);
        }
        том.CopyTo(блок, томС);
        ansi.CopyTo(блок, путьС);
        ansiСуффикс.CopyTo(блок, суффиксС);
        return блок;
    }

    [Fact]
    public void Галка_от_имени_администратора_и_цель_из_LinkInfo()
    {
        var ярлык = ShellLink.Parse(Ярлык(СписокId | ЕстьLinkInfo | ОтАдминистратора));

        Assert.Equal(new ShellLink(ProShow, true), ярлык);
    }

    [Fact]
    public void Без_галки_и_без_списка_ID()
    {
        Assert.Equal(new ShellLink(ProShow, false), ShellLink.Parse(Ярлык(ЕстьLinkInfo)));
    }

    [Fact]
    public void Unicode_путь_важнее_ANSI_и_суффикс_дописывается()
    {
        var ярлык = ShellLink.Parse(Ярлык(СписокId | ЕстьLinkInfo, @"C:\Программы\", юникод: true, суффикс: "proshow.exe"));

        Assert.Equal(@"C:\Программы\proshow.exe", ярлык!.Target);
    }

    [Fact]
    public void Без_LinkInfo_цели_нет_а_галка_известна()
    {
        Assert.Equal(new ShellLink(null, true), ShellLink.Parse(Ярлык(СписокId | ОтАдминистратора)));
    }

    [Fact]
    public void Оборванный_LinkInfo_цели_не_даёт()
    {
        var файл = Ярлык(ЕстьLinkInfo | ОтАдминистратора);

        Assert.Equal(new ShellLink(null, true), ShellLink.Parse(файл.AsSpan(0, 0x4C + 40)));
    }

    [Fact]
    public void Не_ярлык_null()
    {
        var файл = Ярлык(ЕстьLinkInfo);
        файл[4] ^= 0xFF;

        Assert.Null(ShellLink.Parse(файл));
        Assert.Null(ShellLink.Parse(Encoding.ASCII.GetBytes("не ярлык")));
        Assert.Null(ShellLink.Parse([]));
    }
}
