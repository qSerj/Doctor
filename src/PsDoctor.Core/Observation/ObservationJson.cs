using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PsDoctor.Core.Observation;

/// <summary>Одни правила JSON для журнала, потока и данных фактов — те же, что у отчёта приёмки.</summary>
public static class ObservationJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = null,
        WriteIndented = false,

        // Незаданное пишется как null: читатель отличает «не снято» от «такого поля нет».
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower) },
    };

    /// <summary>Пустой объект — данные факта, которому нечего добавить к виду.</summary>
    public static JsonElement Empty { get; } = JsonDocument.Parse("{}").RootElement.Clone();

    public static JsonElement ToElement<T>(T data)
    {
        var element = JsonSerializer.SerializeToElement(data, Options);
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException($"данные факта должны быть объектом JSON, а не {element.ValueKind}", nameof(data));
        }
        return element;
    }
}
