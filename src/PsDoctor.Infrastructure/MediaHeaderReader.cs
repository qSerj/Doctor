using System.Buffers.Binary;
using PsDoctor.Core.Media;

namespace PsDoctor.Infrastructure;

/// <summary>
/// Что удалось понять по началу файла: формат по сигнатуре и, если формат неподвижный, размер.
/// </summary>
/// <param name="FormatId">Опознанный формат или <c>null</c>.</param>
/// <param name="Size">Размер, если формат неподвижный и заголовок цел.</param>
/// <param name="Status">Чем кончилось чтение заголовка.</param>
public readonly record struct HeaderReading(string? FormatId, PixelSize? Size, MediaProbeStatus Status);

/// <summary>
/// Читалки заголовков. Размеров картинок в файле шоу нет, а главное число продукта — сумма
/// распакованных пикселей, поэтому размеры снимаются с самих файлов.
/// </summary>
/// <remarks>
/// Читается только начало файла: десятки байт у PNG и PSD, обход маркеров у JPEG.
/// Полного декодирования здесь нет — это дорогая ступень осмотра, и она про другое.
/// <para>
/// Формат определяется **по сигнатуре**, расширение — вторым: расширение врёт регулярно,
/// и несовпадение само по себе факт, который стоит записать.
/// </para>
/// </remarks>
public static class MediaHeaderReader
{
    /// <summary>Докуда обходим маркеры JPEG, прежде чем признать заголовок битым.</summary>
    private const int JpegScanLimit = 256 * 1024;

    public static HeaderReading Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        Span<byte> head = stackalloc byte[30];
        var read = ReadAtLeast(stream, head);

        if (StartsWith(head, read, [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]))
        {
            return ReadPng(head, read);
        }

        if (StartsWith(head, read, [(byte)'8', (byte)'B', (byte)'P', (byte)'S']))
        {
            return ReadPsd(head, read);
        }

        if (StartsWith(head, read, [0xFF, 0xD8]))
        {
            return ReadJpeg(stream, head, read);
        }

        if (IsGif(head, read))
        {
            return ReadGif(head, read);
        }

        if (IsBmp(head, read))
        {
            return ReadBmp(head, read);
        }

        // Видео опознаём, но не измеряем: размер кадра без ffprobe не снять, и врать об этом незачем.
        if (IsMp4(head, read))
        {
            return new HeaderReading("mp4", null, MediaProbeStatus.NotProbed);
        }

        if (IsAvi(head, read))
        {
            return new HeaderReading("avi", null, MediaProbeStatus.NotProbed);
        }

        return new HeaderReading(null, null, MediaProbeStatus.UnknownFormat);
    }

    /// <summary>PNG: сигнатура, затем чанк IHDR длиной 13, в нём ширина и высота по четыре байта.</summary>
    private static HeaderReading ReadPng(ReadOnlySpan<byte> head, int read)
    {
        if (read < 24 || !head.Slice(12, 4).SequenceEqual("IHDR"u8))
        {
            return new HeaderReading("png", null, MediaProbeStatus.BrokenHeader);
        }

        var width = BinaryPrimitives.ReadUInt32BigEndian(head.Slice(16, 4));
        var height = BinaryPrimitives.ReadUInt32BigEndian(head.Slice(20, 4));

        return Make("png", width, height);
    }

    /// <summary>
    /// PSD: сигнатура, версия, шесть зарезервированных байт, число каналов,
    /// затем <b>высота раньше ширины</b> — главная ловушка этой читалки.
    /// </summary>
    private static HeaderReading ReadPsd(ReadOnlySpan<byte> head, int read)
    {
        if (read < 22)
        {
            return new HeaderReading("psd", null, MediaProbeStatus.BrokenHeader);
        }

        var version = BinaryPrimitives.ReadUInt16BigEndian(head.Slice(4, 2));
        if (version is not (1 or 2))
        {
            return new HeaderReading("psd", null, MediaProbeStatus.BrokenHeader);
        }

        var height = BinaryPrimitives.ReadUInt32BigEndian(head.Slice(14, 4));
        var width = BinaryPrimitives.ReadUInt32BigEndian(head.Slice(18, 4));

        return Make(version == 2 ? "psb" : "psd", width, height);
    }

    /// <summary>
    /// JPEG: обход маркеров. Маркеры D0–D9 и 01 длины не имеют, у остальных длина два байта.
    /// Кадр описывает SOFn — C0–CF, кроме C4, C8 и CC.
    /// </summary>
    private static HeaderReading ReadJpeg(Stream stream, ReadOnlySpan<byte> head, int read)
    {
        // Всё, что успели прочитать сверх двух байт сигнатуры, возвращаем в поток обходом сначала.
        if (!stream.CanSeek)
        {
            return new HeaderReading("jpeg", null, MediaProbeStatus.BrokenHeader);
        }

        stream.Position = 2;

        var scanned = 2;
        Span<byte> pair = stackalloc byte[2];
        Span<byte> frame = stackalloc byte[5];

        while (scanned < JpegScanLimit)
        {
            var b = stream.ReadByte();
            scanned++;

            if (b < 0)
            {
                return new HeaderReading("jpeg", null, MediaProbeStatus.BrokenHeader);
            }

            if (b != 0xFF)
            {
                continue;
            }

            int marker;
            do
            {
                marker = stream.ReadByte();
                scanned++;
            }
            while (marker == 0xFF);

            if (marker < 0)
            {
                return new HeaderReading("jpeg", null, MediaProbeStatus.BrokenHeader);
            }

            // Маркеры без длины: заполнение, перезапуск и начало изображения.
            if (marker is 0x00 or 0x01 or (>= 0xD0 and <= 0xD7))
            {
                continue;
            }

            // Начало данных или конец изображения: кадр так и не встретился.
            if (marker is 0xDA or 0xD9)
            {
                return new HeaderReading("jpeg", null, MediaProbeStatus.BrokenHeader);
            }

            if (ReadAtLeast(stream, pair) != 2)
            {
                return new HeaderReading("jpeg", null, MediaProbeStatus.BrokenHeader);
            }

            scanned += 2;
            var length = BinaryPrimitives.ReadUInt16BigEndian(pair);

            if (length < 2)
            {
                return new HeaderReading("jpeg", null, MediaProbeStatus.BrokenHeader);
            }

            var isFrame = marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC;

            if (!isFrame)
            {
                stream.Position += length - 2;
                scanned += length - 2;
                continue;
            }

            if (ReadAtLeast(stream, frame) != 5)
            {
                return new HeaderReading("jpeg", null, MediaProbeStatus.BrokenHeader);
            }

            var height = BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(1, 2));
            var width = BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(3, 2));

            return Make("jpeg", width, height);
        }

        return new HeaderReading("jpeg", null, MediaProbeStatus.BrokenHeader);
    }

    private static HeaderReading ReadGif(ReadOnlySpan<byte> head, int read)
    {
        if (read < 10)
        {
            return new HeaderReading("gif", null, MediaProbeStatus.BrokenHeader);
        }

        return Make(
            "gif",
            BinaryPrimitives.ReadUInt16LittleEndian(head.Slice(6, 2)),
            BinaryPrimitives.ReadUInt16LittleEndian(head.Slice(8, 2)));
    }

    private static HeaderReading ReadBmp(ReadOnlySpan<byte> head, int read)
    {
        if (read < 26)
        {
            return new HeaderReading("bmp", null, MediaProbeStatus.BrokenHeader);
        }

        var width = BinaryPrimitives.ReadInt32LittleEndian(head.Slice(18, 4));
        var height = Math.Abs(BinaryPrimitives.ReadInt32LittleEndian(head.Slice(22, 4)));

        return Make("bmp", (uint)Math.Max(0, width), (uint)height);
    }

    private static HeaderReading Make(string format, uint width, uint height) =>
        width is 0 or > int.MaxValue || height is 0 or > int.MaxValue
            ? new HeaderReading(format, null, MediaProbeStatus.BrokenHeader)
            : new HeaderReading(format, new PixelSize((int)width, (int)height), MediaProbeStatus.Ok);

    private static bool IsGif(ReadOnlySpan<byte> head, int read) =>
        read >= 6 && head[..3].SequenceEqual("GIF"u8);

    private static bool IsBmp(ReadOnlySpan<byte> head, int read) =>
        read >= 2 && head[0] == (byte)'B' && head[1] == (byte)'M';

    private static bool IsMp4(ReadOnlySpan<byte> head, int read) =>
        read >= 12 && head.Slice(4, 4).SequenceEqual("ftyp"u8);

    private static bool IsAvi(ReadOnlySpan<byte> head, int read) =>
        read >= 12 && head[..4].SequenceEqual("RIFF"u8) && head.Slice(8, 4).SequenceEqual("AVI "u8);

    private static bool StartsWith(ReadOnlySpan<byte> head, int read, ReadOnlySpan<byte> signature) =>
        read >= signature.Length && head[..signature.Length].SequenceEqual(signature);

    private static int ReadAtLeast(Stream stream, Span<byte> buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = stream.Read(buffer[total..]);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }

}
