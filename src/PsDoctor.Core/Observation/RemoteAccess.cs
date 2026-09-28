using System.Net;

namespace PsDoctor.Core.Observation;

/// <summary>
/// Кого наблюдатель пускает дальше проверки ключа: петлю всегда, остальных — только из разрешённых сетей.
/// Ключ едет по сети открытым текстом, поэтому чужая сеть получает отказ, не дойдя до него.
/// </summary>
public static class RemoteAccess
{
    public static bool IsAllowed(IPAddress? remote, IReadOnlyList<IPNetwork> networks)
    {
        ArgumentNullException.ThrowIfNull(networks);
        if (remote is null)
        {
            return false;
        }
        if (remote.IsIPv4MappedToIPv6)
        {
            remote = remote.MapToIPv4();
        }
        return IPAddress.IsLoopback(remote) || networks.Any(network => network.Contains(remote));
    }

    /// <summary>Разбирает сеть вида <c>192.168.0.0/24</c>. Ошибка — строка для человека.</summary>
    public static (IPNetwork? Network, string? Error) ParseNetwork(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return IPNetwork.TryParse(text, out var network)
            ? (network, null)
            : (null, $"ожидается сеть вида 192.168.0.0/24, получено «{text}»");
    }
}
