using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PsDoctor.Core.Reporting;

/// <summary>
/// Запись отчёта. По умолчанию — одна строка на проект: прогон пачки заявлен основным
/// способом пользоваться CLI, а для пачки это родной вид — дописывается, режется,
/// читается построчно и вьювером, и моделью.
/// </summary>
public static class ReportWriter
{
    private static readonly JsonSerializerOptions Compact = Options(indented: false);

    private static readonly JsonSerializerOptions Pretty = Options(indented: true);

    public static string ToJson(Report report, bool pretty = false)
    {
        ArgumentNullException.ThrowIfNull(report);
        return JsonSerializer.Serialize(report, pretty ? Pretty : Compact);
    }

    public static void Write(TextWriter writer, Report report, bool pretty = false)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteLine(ToJson(report, pretty));
    }

    private static JsonSerializerOptions Options(bool indented) => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = null,
        WriteIndented = indented,

        // Незаданное пишется как null, а поле не опускается: читатель обязан отличать
        // «доктор такого поля не знает» от «проект этого не задал».
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,

        // Кириллица в отчёте остаётся кириллицей: отчёт машинный, но не нечитаемый,
        // а экранирование мешает и вьюверу, и проверке обезличивания.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
