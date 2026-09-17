using System.Text.Json;

namespace PsDoctor.Core.Observation;

/// <summary>
/// Запись журнала сеанса. Слов в факте нет: вид — устойчивое имя, данные — то, что снято с программы.
/// Новый вид факта — новый <see cref="Kind"/>, а не новая версия протокола.
/// </summary>
/// <param name="Number">Номер по порядку в сеансе: с единицы и без пропусков. С него клиент переподключается.</param>
/// <param name="Elapsed">Время от старта сеанса. Его приносит наблюдатель: ядро часов не знает.</param>
/// <param name="ProcessId">Процесс, к которому относится факт, если относится.</param>
/// <param name="Data">Данные факта — всегда объект JSON, у факта без данных пустой.</param>
public sealed record Fact(long Number, TimeSpan Elapsed, string Session, int? ProcessId, string Kind, JsonElement Data);
