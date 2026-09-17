using System.Net;

namespace PsDoctor.Observer;

/// <summary>
/// Ключи запуска наблюдателя. Слушает 127.0.0.1, пока сетевой адрес не задан явно; ключ Bearer
/// обязателен всегда, поэтому без ключа не открывается ни сетевой адрес, ни петля.
/// </summary>
/// <param name="DataDirectory">Каталог журналов сеансов; <c>null</c> — <see cref="DefaultDataDirectory"/>.</param>
/// <param name="ProgramPath">Программа, которую запускает <c>launch</c>; <c>null</c> — ProShow на обычном месте.</param>
public sealed record ObserverOptions(IPAddress Address, int Port, string Key, string? DataDirectory = null, string? ProgramPath = null)
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
                default:
                    return (null, $"незнакомый ключ или нет значения: «{args[i]}»");
            }
        }

        if (keyFile is null)
            return (null, "--key-file обязателен: без ключа наблюдатель не слушает");
        if (!File.Exists(keyFile))
            return (null, $"нет файла ключа: {keyFile}");

        var key = File.ReadAllText(keyFile).Trim();
        if (key.Length == 0)
            return (null, $"файл ключа пуст: {keyFile}");

        return (new ObserverOptions(address, port, key, data, program), null);
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
