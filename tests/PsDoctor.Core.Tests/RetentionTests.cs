using System.Text.Json;
using PsDoctor.Core.Observation;
using Xunit;

namespace PsDoctor.Core.Tests;

public sealed class RetentionTests
{
    private static readonly DateTime Сейчас = new(2026, 9, 25, 12, 0, 0, DateTimeKind.Utc);
    private static readonly RetentionLimits Пределы = new(TimeSpan.FromDays(30), 1000, TimeSpan.FromDays(180));

    private static StoredSession Сеанс(string id, int днейНазад, long байтов = 10, bool помечен = false) =>
        new(id, Сейчас.AddDays(-днейНазад), байтов, помечен);

    [Fact]
    public void Обычный_сеанс_старше_срока_удаляется_помеченный_остаётся()
    {
        var удалить = Retention.Expired([Сеанс("обычный", 31), Сеанс("мастер", 31, помечен: true), Сеанс("свежий", 1)], Пределы, Сейчас);

        Assert.Equal(new[] { "обычный" }, удалить);
    }

    [Fact]
    public void Помеченный_сеанс_уходит_по_своему_сроку()
    {
        var удалить = Retention.Expired([Сеанс("мастер", 181, помечен: true), Сеанс("эпизод", 179, помечен: true)], Пределы, Сейчас);

        Assert.Equal(new[] { "мастер" }, удалить);
    }

    [Fact]
    public void Сверх_объёма_уходят_старейшие_обычные_раньше_помеченных()
    {
        var удалить = Retention.Expired(
            [Сеанс("мастер", 10, 400, помечен: true), Сеанс("старый", 9, 400), Сеанс("средний", 5, 400), Сеанс("новый", 1, 100)],
            Пределы, Сейчас);

        // 1300 байт при пределе 1000: одного старейшего обычного хватает, помеченный старше, но остаётся.
        Assert.Equal(new[] { "старый" }, удалить);
    }

    [Fact]
    public void Помеченные_уходят_по_объёму_только_когда_обычных_не_осталось()
    {
        var удалить = Retention.Expired(
            [Сеанс("мастер-1", 10, 600, помечен: true), Сеанс("мастер-2", 5, 600, помечен: true), Сеанс("обычный", 1, 100)],
            Пределы, Сейчас);

        Assert.Equal(new[] { "обычный", "мастер-1" }, удалить);
    }

    [Fact]
    public void В_пределах_ничего_не_удаляется()
    {
        Assert.Empty(Retention.Expired([Сеанс("а", 29, 500), Сеанс("б", 0, 500)], Пределы, Сейчас));
    }

    private static Fact Факт(string вид, string данные) =>
        new(1, TimeSpan.Zero, "s", null, вид, JsonDocument.Parse(данные).RootElement.Clone());

    [Fact]
    public void Помечен_сеанс_мастера_и_сеанс_с_эпизодом()
    {
        Assert.True(Retention.IsMarked([Факт(ProgramFactKinds.SessionStarted, """{"origin":"wizard"}""")]));
        Assert.True(Retention.IsMarked([Факт(ProgramFactKinds.SessionStarted, """{"origin":null}"""), Факт(ProgramFactKinds.Episode, "{}")]));
        Assert.False(Retention.IsMarked([Факт(ProgramFactKinds.SessionStarted, """{"origin":null}""")]));
        Assert.False(Retention.IsMarked([Факт(ProgramFactKinds.SessionStarted, """{"origin":"workbench"}""")]));
    }
}
