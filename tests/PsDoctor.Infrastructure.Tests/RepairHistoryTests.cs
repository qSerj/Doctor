using PsDoctor.Infrastructure.Observation;
using Xunit;

namespace PsDoctor.Infrastructure.Tests;

public sealed class RepairHistoryTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("psdoctor-history-").FullName;

    public void Dispose() => Directory.Delete(root, recursive: true);

    [Fact]
    public void Нумерует_попытки_и_находит_ожидающую_ответа()
    {
        var history = new RepairHistory(root);
        history.TryAppend(new RepairHistoryEvent(DateTimeOffset.UtcNow, "attempt", 1, "load", "show.psh"));

        Assert.Equal(2, history.NextAttempt());
        Assert.Equal(1, history.PendingAttempt(DateTimeOffset.UtcNow)!.Attempt);
    }

    [Fact]
    public void Считает_только_подтверждённые_неудачи_симптома()
    {
        var history = new RepairHistory(root);
        history.TryAppend(new RepairHistoryEvent(DateTimeOffset.UtcNow, "attempt", 1, "load"));
        history.TryAppend(new RepairHistoryEvent(DateTimeOffset.UtcNow, "feedback", 1, "load", Result: "unresolved"));
        history.TryAppend(new RepairHistoryEvent(DateTimeOffset.UtcNow, "attempt", 2, "load"));
        history.TryAppend(new RepairHistoryEvent(DateTimeOffset.UtcNow, "feedback", 2, "load", Result: "resolved"));
        history.TryAppend(new RepairHistoryEvent(DateTimeOffset.UtcNow, "attempt", 3, "render"));
        history.TryAppend(new RepairHistoryEvent(DateTimeOffset.UtcNow, "feedback", 3, "render", Result: "unresolved"));

        Assert.Equal(1, history.UnresolvedAttempts("load", DateTimeOffset.UtcNow));
        Assert.Equal(1, history.UnresolvedAttempts("render", DateTimeOffset.UtcNow));
        Assert.Null(history.PendingAttempt(DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Неудачи_старше_двух_недель_не_считаются()
    {
        var сейчас = new DateTimeOffset(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);
        var history = new RepairHistory(root);
        history.TryAppend(new RepairHistoryEvent(сейчас.AddDays(-20), "attempt", 1, "problem"));
        history.TryAppend(new RepairHistoryEvent(сейчас.AddDays(-19), "feedback", 1, "problem", Result: "unresolved"));
        history.TryAppend(new RepairHistoryEvent(сейчас.AddDays(-3), "attempt", 2, "problem"));
        history.TryAppend(new RepairHistoryEvent(сейчас.AddDays(-3), "feedback", 2, "problem", Result: "unresolved"));

        Assert.Equal(1, history.UnresolvedAttempts("problem", сейчас));
    }

    [Fact]
    public void Про_попытку_старше_двух_суток_не_спрашивают_а_пишут_unknown()
    {
        var сейчас = new DateTimeOffset(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);
        var history = new RepairHistory(root);
        history.TryAppend(new RepairHistoryEvent(сейчас.AddDays(-3), "attempt", 1, "problem"));

        Assert.Null(history.PendingAttempt(сейчас));
        Assert.Equal(1, history.ExpireFeedback(сейчас));
        Assert.Equal(0, history.ExpireFeedback(сейчас));
        var ответ = Assert.Single(history.Read(), item => item.Event == "feedback");
        Assert.Equal(1, ответ.Attempt);
        Assert.Equal("unknown", ответ.Result);
        Assert.Equal(0, history.UnresolvedAttempts("problem", сейчас));
    }

    [Fact]
    public void Свежая_попытка_ждёт_ответа_и_не_истекает()
    {
        var сейчас = new DateTimeOffset(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);
        var history = new RepairHistory(root);
        history.TryAppend(new RepairHistoryEvent(сейчас.AddHours(-30), "attempt", 1, "problem"));

        Assert.Equal(0, history.ExpireFeedback(сейчас));
        Assert.Equal(1, history.PendingAttempt(сейчас)!.Attempt);
    }

    [Fact]
    public void Метка_без_наблюдателя_не_меняет_счёт_попыток()
    {
        var сейчас = new DateTimeOffset(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);
        var history = new RepairHistory(root);
        history.TryAppend(new RepairHistoryEvent(сейчас, "incident", 0, "problem", Result: "observer-unavailable"));

        Assert.Equal(1, history.NextAttempt());
        Assert.Null(history.PendingAttempt(сейчас));
        Assert.Equal(0, history.UnresolvedAttempts("problem", сейчас));
    }
}
