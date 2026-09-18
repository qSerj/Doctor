using System.Collections.Concurrent;

namespace PsDoctor.Workbench.Tests;

/// <summary>
/// Один поток с очередью продолжений — то же, чем для пульта служит поток интерфейса Avalonia.
/// Без него тест и лента фактов меняли бы списки с разных потоков, и проверялось бы не то, что работает.
/// </summary>
internal sealed class ОдинПоток : SynchronizationContext
{
    private readonly BlockingCollection<(SendOrPostCallback Обработчик, object? Состояние)> очередь = new();

    /// <summary>Выполняет тело на этом потоке, прокачивая очередь, пока тело не кончится.</summary>
    public static void Выполнить(Func<Task> тело, TimeSpan? предел = null)
    {
        ArgumentNullException.ThrowIfNull(тело);

        var прежний = Current;
        var контекст = new ОдинПоток();
        SetSynchronizationContext(контекст);
        using var срок = new Timer(_ => контекст.Закрыть(), null, предел ?? TimeSpan.FromSeconds(120), Timeout.InfiniteTimeSpan);
        try
        {
            var задача = тело();
            задача.ContinueWith(
                _ => контекст.Закрыть(),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            foreach (var (обработчик, состояние) in контекст.очередь.GetConsumingEnumerable())
            {
                обработчик(состояние);
            }
            if (!задача.IsCompleted)
            {
                throw new TimeoutException("тест не кончился в отведённое время");
            }
            задача.GetAwaiter().GetResult();
        }
        finally
        {
            SetSynchronizationContext(прежний);
        }
    }

    /// <summary>Ждёт условия, отдавая поток очереди: так тест видит то, что делает лента фактов.</summary>
    public static async Task ЖдатьAsync(Func<bool> условие, string чего, TimeSpan? предел = null)
    {
        ArgumentNullException.ThrowIfNull(условие);

        var срок = DateTime.UtcNow + (предел ?? TimeSpan.FromSeconds(30));
        while (!условие())
        {
            if (DateTime.UtcNow > срок)
            {
                throw new TimeoutException($"не дождались: {чего}");
            }
            await Task.Delay(5);
        }
    }

    public override void Post(SendOrPostCallback d, object? state)
    {
        try
        {
            очередь.Add((d, state));
        }
        catch (InvalidOperationException)
        {
            // Очередь закрыта: тело теста кончилось, хвостовые продолжения никому не нужны.
        }
    }

    public override void Send(SendOrPostCallback d, object? state)
    {
        ArgumentNullException.ThrowIfNull(d);
        d(state);
    }

    private void Закрыть()
    {
        try
        {
            очередь.CompleteAdding();
        }
        catch (ObjectDisposedException)
        {
        }
    }
}
