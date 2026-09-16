using System.Text;
using PsDoctor.Core;

namespace PsDoctor.Infrastructure;

/// <summary>
/// CP1251 не входит в набор кодировок .NET по умолчанию: без регистрации провайдера
/// кириллические пути внутри файла шоу читаются как мусор, и живые файлы объявляются потерянными.
/// </summary>
public static class ShowFileEncoding
{
    private static readonly Lazy<Encoding> Lazy = new(() =>
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(ShowFile.CodePage);
    });

    public static Encoding Cp1251 => Lazy.Value;

    /// <summary>
    /// Открывает файл шоу в CP1251 и только в ней.
    /// </summary>
    /// <remarks>
    /// Распознавание метки порядка байт выключено намеренно. Решение «CP1251 — единственная дверь»
    /// не должно молча отменяться тремя байтами в начале чужого файла: программа пишет файл шоу
    /// в кодовой странице, а не в Юникоде, и файл с меткой — это повод сказать об этом,
    /// а не повод прочитать его иначе.
    /// </remarks>
    public static StreamReader OpenRead(string path) =>
        new(File.OpenRead(path), Cp1251, detectEncodingFromByteOrderMarks: false);

    /// <summary>
    /// Начинается ли файл с метки порядка байт. Метка означает, что файл кто-то пересохранил
    /// чужим редактором, и это факт о проекте, а не повод менять кодировку чтения.
    /// </summary>
    public static bool HasByteOrderMark(string path)
    {
        using var stream = File.OpenRead(path);

        Span<byte> head = stackalloc byte[3];
        var read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);

        return read == 3 && head is [0xEF, 0xBB, 0xBF];
    }
}
