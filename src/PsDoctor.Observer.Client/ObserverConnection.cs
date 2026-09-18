namespace PsDoctor.Observer.Client;

/// <summary>
/// Откуда клиент берёт адрес и ключ. Имена переменных общие для всех клиентов: агент из оболочки и пульт
/// владельца ходят на один наблюдатель, и вторая копия имён однажды разошлась бы с первой.
/// </summary>
public static class ObserverConnection
{
    public const string UrlVariable = "PSDOCTOR_OBSERVER_URL";

    public const string KeyFileVariable = "PSDOCTOR_OBSERVER_KEY_FILE";
}
