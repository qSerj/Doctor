using PsDoctor.Core.Media;
using PsDoctor.Infrastructure;
using Xunit;

namespace PsDoctor.Infrastructure.Tests;

/// <summary>
/// Читалки заголовков проверяются байтовыми литералами: файлов-образцов в репозитории нет,
/// а заголовок — это десяток байт, которые проще написать, чем положить картинкой.
/// </summary>
public sealed class MediaHeaderReaderTests
{
    private static HeaderReading Read(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        return MediaHeaderReader.Read(stream);
    }

    private static byte[] Png(uint width, uint height) =>
    [
        0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A,
        0, 0, 0, 13,
        (byte)'I', (byte)'H', (byte)'D', (byte)'R',
        .. BigEndian(width),
        .. BigEndian(height),
        8, 6, 0, 0, 0,
    ];

    private static byte[] Psd(uint width, uint height, ushort version = 1) =>
    [
        (byte)'8', (byte)'B', (byte)'P', (byte)'S',
        (byte)(version >> 8), (byte)version,
        0, 0, 0, 0, 0, 0,
        0, 3,
        .. BigEndian(height),
        .. BigEndian(width),
        0, 8,
        0, 3,
    ];

    /// <summary>JPEG с парой ничего не значащих отрезков перед кадром — как в настоящих файлах.</summary>
    private static byte[] Jpeg(ushort width, ushort height, byte frameMarker = 0xC0) =>
    [
        0xFF, 0xD8,
        0xFF, 0xE0, 0, 16, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0, 1, 1, 0, 0, 1, 0, 1, 0, 0,
        0xFF, 0xDB, 0, 4, 0, 0,
        0xFF, frameMarker, 0, 17, 8,
        (byte)(height >> 8), (byte)height,
        (byte)(width >> 8), (byte)width,
        3, 1, 0x22, 0, 2, 0x11, 1, 3, 0x11, 1,
        0xFF, 0xDA, 0, 2,
    ];

    private static byte[] BigEndian(uint value) =>
        [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];

    [Fact]
    public void PNG_читается_из_IHDR()
    {
        var reading = Read(Png(3840, 2160));

        Assert.Equal(MediaProbeStatus.Ok, reading.Status);
        Assert.Equal("png", reading.FormatId);
        Assert.Equal(new PixelSize(3840, 2160), reading.Size);
    }

    [Fact]
    public void PSD_читает_высоту_раньше_ширины()
    {
        // Главная ловушка этой читалки: в заголовке PSD сначала идёт высота.
        var reading = Read(Psd(width: 4000, height: 3871));

        Assert.Equal(MediaProbeStatus.Ok, reading.Status);
        Assert.Equal(new PixelSize(4000, 3871), reading.Size);
    }

    [Fact]
    public void PSB_опознаётся_отдельно_от_PSD()
    {
        Assert.Equal("psb", Read(Psd(100, 200, version: 2)).FormatId);
    }

    [Fact]
    public void JPEG_читается_после_обхода_маркеров()
    {
        var reading = Read(Jpeg(1920, 1200));

        Assert.Equal(MediaProbeStatus.Ok, reading.Status);
        Assert.Equal("jpeg", reading.FormatId);
        Assert.Equal(new PixelSize(1920, 1200), reading.Size);
    }

    [Theory]
    [InlineData((byte)0xC0)]
    [InlineData((byte)0xC1)]
    [InlineData((byte)0xC2)]
    [InlineData((byte)0xC9)]
    public void Кадр_JPEG_опознаётся_у_всех_видов_SOF(byte marker)
    {
        Assert.Equal(new PixelSize(640, 480), Read(Jpeg(640, 480, marker)).Size);
    }

    [Fact]
    public void Таблица_Хаффмана_за_кадр_не_принимается()
    {
        // C4 — таблица Хаффмана, не кадр. Принять её за кадр значит прочитать размер из мусора.
        var reading = Read(Jpeg(640, 480, frameMarker: 0xC4));

        Assert.Equal(MediaProbeStatus.BrokenHeader, reading.Status);
        Assert.Null(reading.Size);
    }

    [Fact]
    public void Обрезанный_заголовок_даёт_битый_а_не_исключение()
    {
        Assert.Equal(MediaProbeStatus.BrokenHeader, Read(Png(100, 100)[..14]).Status);
        Assert.Equal(MediaProbeStatus.BrokenHeader, Read(Psd(100, 100)[..10]).Status);
        Assert.Equal(MediaProbeStatus.BrokenHeader, Read([0xFF, 0xD8, 0xFF]).Status);
    }

    [Fact]
    public void Нулевой_размер_считается_битым_заголовком()
    {
        Assert.Equal(MediaProbeStatus.BrokenHeader, Read(Png(0, 100)).Status);
    }

    [Fact]
    public void Пустой_файл_даёт_неизвестный_формат()
    {
        Assert.Equal(MediaProbeStatus.UnknownFormat, Read([]).Status);
    }

    [Fact]
    public void Видео_опознаётся_но_не_измеряется_и_так_и_сказано()
    {
        byte[] mp4 = [0, 0, 0, 24, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'i', (byte)'s', (byte)'o', (byte)'m', 0, 0, 0, 0];
        byte[] avi = [(byte)'R', (byte)'I', (byte)'F', (byte)'F', 0, 0, 0, 0, (byte)'A', (byte)'V', (byte)'I', (byte)' ', 0, 0, 0, 0];

        Assert.Equal(MediaProbeStatus.NotProbed, Read(mp4).Status);
        Assert.Equal("mp4", Read(mp4).FormatId);
        Assert.Equal(MediaProbeStatus.NotProbed, Read(avi).Status);
        Assert.Equal("avi", Read(avi).FormatId);
    }

    [Fact]
    public void Расширение_врёт_формат_берётся_по_сигнатуре()
    {
        // Файл называется .jpg, а внутри PNG. Формат определяет сигнатура.
        Assert.Equal("png", Read(Png(10, 10)).FormatId);
    }

    [Fact]
    public void GIF_и_BMP_тоже_читаются()
    {
        byte[] gif = [(byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a', 0x20, 0x03, 0x84, 0x01];
        Assert.Equal(new PixelSize(800, 388), Read(gif).Size);

        byte[] bmp = new byte[30];
        bmp[0] = (byte)'B';
        bmp[1] = (byte)'M';
        BitConverter.GetBytes(1024).CopyTo(bmp, 18);
        BitConverter.GetBytes(-768).CopyTo(bmp, 22);
        Assert.Equal(new PixelSize(1024, 768), Read(bmp).Size);
    }
}
