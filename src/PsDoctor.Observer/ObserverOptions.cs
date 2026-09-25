using System.Globalization;
using System.Net;
using PsDoctor.Core.Observation;

namespace PsDoctor.Observer;

/// <summary>
/// Ключи запуска наблюдателя. Слушает 127.0.0.1, пока сетевой адрес не задан явно; ключ Bearer
/// обязателен всегда, поэтому без ключа не открывается ни сетевой адрес, ни петля. Не-петлевой адрес
/// вдобавок требует <c>--allow-remote</c>: за этим API стоит «запусти программу» и «отдай все журналы»,
/// а TLS у него нет и ключ едет по сети открытым текстом.
/// </summary>
/// <param name="DataDirectory">Каталог журналов сеансов; <c>null</c> — <see cref="DefaultDataDirectory"/>.</param>
/// <param name="ProgramPath">Программа, которую запускает <c>launch</c>; <c>null</c> — ProShow на обычном месте.</param>
/// <param name="Retention">
/// Пределы хранения из <c>--keep-days</c>, <c>--keep-mb</c>, <c>--keep-marked-days</c>; незаданный ключ берёт значение
/// установщика по умолчанию. Ни одного ключа — <c>null</c>: наблюдатель стенда ничего не удаляет.
/// </param>
public sealed record ObserverOptions(IPAddress Address, int Port, string Key, string? DataDirectory = null, string? ProgramPath = null,
    RetentionLimits? Retention = null)
{
    public const int DefaultPort = 8100;

    /// <summary>Журналы сеансов в профиле пользователя, от имени которого работает наблюдатель.</summary>
    public static string DefaultDataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PsDoctor", "observer", "sessions");

    /// <summary>Разбирает ключи. Ошибка — строка для stderr, а не исключение.</summary>
    public static (ObserverOptions? Options, string? Error) Parse(IReadOnlyList<string> args)
    {
        var address = IPAddress.Loopback;
        var port = DefaultPort;
        string? keyFile = null;
        string? data = null;
        string? program = null;
        var allowRemote = false;
        double? keepDays = null, keepMegabytes = null, keepMarkedDays = null;

        for (var i = 0; i < args.Count; i++)
        {
            var value = i + 1 < args.Count ? args[i + 1] : null;
            switch (args[i])
            {
                case "--listen" when value is not null:
                    if (!TryParseEndpoint(value, out address, out port))
                        return (null, $"--listen: ожидается адрес:порт, получено «{value}»");
                    i++;
                    break;
                case "--key-file" when value is not null:
                    keyFile = value;
                    i++;
                    break;
                case "--data" when value is not null:
                    data = value;
                    i++;
                    break;
                case "--program" when value is not null:
                    program = value;
                    i++;
                    break;
                case "--keep-days" or "--keep-mb" or "--keep-marked-days" when value is not null:
                    if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || number <= 0)
                        return (null, $"{args[i]}: ожидается положительное число, получено «{value}»");
                    switch (args[i])
                    {
                        case "--keep-days": keepDays = number; break;
                        case "--keep-mb": keepMegabytes = number; break;
                        default: keepMarkedDays = number; break;
                    }
                    i++;
                    break;
                case "--allow-remote":
                    allowRemote = true;
                    break;
                default:
                    return (null, $"незнакомый ключ или нет значения: «{args[i]}»");
            }
        }

        if (!IPAddress.IsLoopback(address) && !allowRemote)
            return (null, $"--listen {address}: не-петлевой адрес открывается только с --allow-remote");
        if (keyFile is null)
            return (null, "--key-file обязателен: без ключа наблюдатель не слушает");
        if (!File.Exists(keyFile))
            return (null, $"нет файла ключа: {keyFile}");

        var key = File.ReadAllText(keyFile).Trim();
        if (key.Length == 0)
            return (null, $"файл ключа пуст: {keyFile}");

        RetentionLimits? retention = null;
        if (keepDays is not null || keepMegabytes is not null || keepMarkedDays is not null)
        {
            var d = InstalledSettings.Default.Retention;
            retention = new RetentionLimits(
                keepDays is { } days ? TimeSpan.FromDays(days) : d.Age,
                keepMegabytes is { } megabytes ? (long)(megabytes * 1024 * 1024) : d.Bytes,
                keepMarkedDays is { } markedDays ? TimeSpan.FromDays(markedDays) : d.MarkedAge);
        }

        return (new ObserverOptions(address, port, key, data, program, retention), null);
    }

    private static bool TryParseEndpoint(string text, out IPAddress address, out int port)
    {
        address = IPAddress.Loopback;
        port = 0;
        var colon = text.LastIndexOf(':');
        return colon > 0
            && IPAddress.TryParse(text[..colon], out address!)
            && int.TryParse(text[(colon + 1)..], out port)
            && port is >= 0 and <= 65535;
    }
}
