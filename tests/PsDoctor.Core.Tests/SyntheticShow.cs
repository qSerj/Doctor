using PsDoctor.Core.Format;

namespace PsDoctor.Core.Tests;

/// <summary>
/// Синтетические файлы шоу для тестов собираются строками в коде, а не лежат файлами.
/// Парсер принимает текст, кодировка в этом слое не участвует, а текстовый образец
/// в CP1251 внутри гита — постоянный источник неприятностей.
/// Имён людей и названий работ здесь не бывает, как и везде.
/// </summary>
internal static class SyntheticShow
{
    public static ParseResult Parse(params string[] lines)
    {
        var text = ShowFile.Magic + "\r\n" + string.Join("\r\n", lines) + "\r\n";
        using var reader = new StringReader(text);
        return ShowFileParser.Parse(reader);
    }

    public static ShowDocument Document(params string[] lines)
    {
        var result = Parse(lines);
        return result.Document ?? throw new InvalidOperationException("сигнатура синтетического шоу не совпала");
    }
}
