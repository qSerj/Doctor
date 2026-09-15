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

    public static StreamReader OpenRead(string path) =>
        new(File.OpenRead(path), Cp1251);
}
