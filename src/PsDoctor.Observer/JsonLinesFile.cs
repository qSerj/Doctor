using System.Text;
using System.Text.Json;
using PsDoctor.Core.Observation;

namespace PsDoctor.Observer;

/// <summary>
/// Файлы наблюдателя, которые только дописываются строками JSON: метки инцидентов, события журнала Windows. Замок —
/// у вызывающего.
/// </summary>
internal static class JsonLinesFile
{
    /// <summary>
    /// Дописывает значения строками и сбрасывает их на диск. Прошлая запись оборвана посередине строки — новая
    /// начинается с новой строки, а не склеивается с ней.
    /// </summary>
    public static void Append<T>(string path, IEnumerable<T> values)
    {
        var text = new StringBuilder();
        foreach (var value in values)
        {
            text.Append(JsonSerializer.Serialize(value, ObservationJson.Options)).Append('\n');
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        if (stream.Length > 0)
        {
            stream.Seek(-1, SeekOrigin.End);
            if (stream.ReadByte() != '\n')
            {
                stream.WriteByte((byte)'\n');
            }
        }
        stream.Seek(0, SeekOrigin.End);
        stream.Write(Encoding.UTF8.GetBytes(text.ToString()));
        stream.Flush(flushToDisk: true);
    }

    /// <summary>
    /// Значения по порядку. Битая строка, в том числе недописанный хвост после обрыва, пропускается, как и строка,
    /// которую <paramref name="valid"/> не признал своей. Файла нет — пусто.
    /// </summary>
    public static List<T> Read<T>(string path, Func<T, bool> valid)
    {
        var result = new List<T>();
        if (!File.Exists(path))
        {
            return result;
        }
        foreach (var line in File.ReadAllLines(path, Encoding.UTF8))
        {
            try
            {
                if (JsonSerializer.Deserialize<T>(line, ObservationJson.Options) is { } value && valid(value))
                {
                    result.Add(value);
                }
            }
            catch (JsonException)
            {
            }
        }
        return result;
    }
}
