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
        Assert.Equal(1, history.PendingAttempt()!.Attempt);
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

        Assert.Equal(1, history.UnresolvedAttempts("load"));
        Assert.Equal(1, history.UnresolvedAttempts("render"));
        Assert.Null(history.PendingAttempt());
    }
}
