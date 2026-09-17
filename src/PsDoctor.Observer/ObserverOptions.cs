using System.Net;

namespace PsDoctor.Observer;

/// <summary>
/// Ключи запуска наблюдателя. Слушает 127.0.0.1, пока сетевой адрес не задан явно; ключ Bearer
/// обязателен всегда, поэтому без ключа не открывается ни сетевой адрес, ни петля.
/// </summary>
public sealed record ObserverOptions(IPAddress Address, int Port, string Key)
{
    public const int DefaultPort = 8100;

    /// <summary>Разбирает ключи. Ошибка — строка для stderr, а не исключение.</summary>
    public static (ObserverOptions? Options, string? Error) Parse(IReadOnlyList<string> args)
    {
        var address = IPAddress.Loopback;
        var port = DefaultPort;
        string? keyFile = null;

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

        return (new ObserverOptions(address, port, key), null);
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
