namespace PsDoctor.Core;

/// <summary>
/// Опознание файла шоу по первой строке. Всё остальное знание о формате появится на Э1;
/// здесь только то, что проверено глазами на боевом проекте.
/// </summary>
public static class ShowFile
{
    /// <summary>Первая строка файла шоу, дословно, без завершающего CRLF.</summary>
    public const string Magic = "Photodex(R) ProShow(TM) Producer Show File Version=0";

    /// <summary>Расширение текстового описания шоу.</summary>
    public const string Extension = ".psh";

    /// <summary>Кодовая страница, в которой записана кириллица внутри файла.</summary>
    public const int CodePage = 1251;

    public static bool LooksLikeShowFile(string firstLine) =>
        firstLine.TrimEnd('\r', '\n') == Magic;
}
