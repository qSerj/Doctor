using System.Globalization;
using System.Text.Json;

namespace PsDoctor.Core.Tests;

/// <summary>
/// Ожидаемые числа боевого проекта лежат файлом рядом с самим проектом, а не в коде теста:
/// имя проекта содержит название клиентской работы, и в открытом репозитории его быть не должно.
/// Нет файла — числовая часть молча не выполняется, а инварианты продолжают работать.
/// </summary>
internal sealed class ExpectedNumbers
{
    /// <summary>
    /// Имена, которые тест умеет проверять. Закрытый список нужен затем, чтобы опечатка
    /// в файле ожидаемого не проходила молча: непроверяемое число — это не проверка.
    /// </summary>
    private static readonly string[] Names =
    [
        "строк",
        "слайдов",
        "слоёвВСлайдах",
        "слоёвВПереходах",
        "слоёвВсего",
        "заменяемыхМестШаблона",
        "ключевыхКадров",
        "суммаДлительностейСлайдовМс",
        "ссылокНаМедиа",
        "уникальныхСсылок",
        "уникальныхКартинок",
        "слоёвВидео",
        "слоёвСГрадиентом",
        "маскирующихСлоёв",
        "звуковыхДорожек",
        "стилейПодписей",
        "подписей",
        "модификаторов",
        "ссылокСоВрущимРасширением",
        "распакованныхБайт",
        "сПотолком1920х1080Байт",
    ];

    private readonly Dictionary<string, double> _values;
    private readonly Dictionary<string, int> _findings;

    private ExpectedNumbers(Dictionary<string, double> values, Dictionary<string, int> findings)
    {
        _values = values;
        _findings = findings;
    }

    /// <summary>Раздел файла, в котором записано, сколько раз сработало каждое правило.</summary>
    public const string FindingsSection = "находок";

    public const string FileName = "ожидаемое.json";

    /// <summary>Читает файл ожидаемого рядом с файлом шоу. Возвращает null, когда его нет.</summary>
    public static ExpectedNumbers? Beside(string showFilePath)
    {
        var directory = Path.GetDirectoryName(showFilePath);
        if (directory is null)
        {
            return null;
        }

        var path = Path.Combine(directory, FileName);
        if (!File.Exists(path))
        {
            return null;
        }

        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        var values = new Dictionary<string, double>(StringComparer.Ordinal);
        var findings = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (property.NameEquals(FindingsSection))
            {
                foreach (var rule in property.Value.EnumerateObject())
                {
                    findings.Add(rule.Name, rule.Value.GetInt32());
                }

                continue;
            }

            if (!Names.Contains(property.Name, StringComparer.Ordinal))
            {
                throw new InvalidOperationException(
                    $"в {FileName} есть число «{property.Name}», которое тест проверять не умеет");
            }

            values.Add(property.Name, property.Value.GetDouble());
        }

        return new ExpectedNumbers(values, findings);
    }

    /// <summary>
    /// Правила, для которых рядом с проектом записано ожидаемое число срабатываний.
    /// Записано не для всех: у проекта без медиа числа правил, зависящих от медиа, были бы
    /// числами про отсутствие медиа, а не про сам проект.
    /// </summary>
    public IEnumerable<string> FindingRules => _findings.Keys;

    public int ExpectedFindings(string ruleId) => _findings[ruleId];

    /// <summary>Сверяет число, если оно в файле названо. Не названо — молча пропускает.</summary>
    public void Check(string name, long actual, string label)
    {
        if (!Names.Contains(name, StringComparer.Ordinal))
        {
            throw new InvalidOperationException($"«{name}» нет в списке проверяемых чисел");
        }

        if (!_values.TryGetValue(name, out var expected))
        {
            return;
        }

        if (Math.Abs(expected - actual) > 0.5)
        {
            throw new InvalidOperationException(
                $"{label}: «{name}» ожидалось {expected.ToString("0.###", CultureInfo.InvariantCulture)}, "
                + $"получилось {actual.ToString(CultureInfo.InvariantCulture)}");
        }
    }

    public bool Has(string name) => _values.ContainsKey(name);
}
