using System.Globalization;
using System.Text.RegularExpressions;

namespace PsDoctor.Observer;

/// <summary>
/// Имена сеансов — время открытия по UTC до миллисекунд: сортируются как строки и годятся в имя файла.
/// Имя из запроса проверяется по образцу, прежде чем стать путём, — иначе запрос читал бы чужие файлы.
/// </summary>
public static partial class SessionIds
{
    public const string JournalExtension = ".jsonl";

    public static string New(DateTime utcNow, Func<string, bool> taken)
    {
        var id = utcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
        if (!taken(id))
        {
            return id;
        }
        for (var n = 2; ; n++)
        {
            var candidate = $"{id}-{n}";
            if (!taken(candidate))
            {
                return candidate;
            }
        }
    }

    public static bool IsValid(string? id) => id is not null && Pattern().IsMatch(id);

    [GeneratedRegex(@"^\d{8}-\d{6}-\d{3}(-\d+)?$")]
    private static partial Regex Pattern();
}
