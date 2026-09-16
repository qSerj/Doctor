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

    private ExpectedNumbers(Dictionary<string, double> values) => _values = values;

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

        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (!Names.Contains(property.Name, StringComparer.Ordinal))
            {
                throw new InvalidOperationException(
                    $"в {FileName} есть число «{property.Name}», которое тест проверять не умеет");
            }

            values.Add(property.Name, property.Value.GetDouble());
        }

        return new ExpectedNumbers(values);
    }

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
