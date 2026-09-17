using System.Diagnostics;
using System.Runtime.Versioning;
using PsDoctor.Core.Observation;
using PsDoctor.Core.Scenarios;
using static PsDoctor.Infrastructure.Observation.Win32Windows;

namespace PsDoctor.Infrastructure.Observation;

/// <summary>
/// Живой запуск: куст под заданием, окна куста и действия с ними — <c>press</c> и <c>close</c>.
/// Мышь, клавиатура и фокус не трогаются ни одним действием.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ProgramRun : IProgramRun
{
    /// <summary>Сколько ждать итога вызова Invoke.</summary>
    public static readonly TimeSpan InvokeTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Сколько повторять Invoke, отвечающий сбоем, пока диалог жив.</summary>
    public static readonly TimeSpan RetryWindow = TimeSpan.FromSeconds(3);

    private static readonly TimeSpan RetryPause = TimeSpan.FromMilliseconds(250);

    /// <summary>Сколько ждать, пока нажатый диалог закроется.</summary>
    public static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(10);

    private readonly JobRun job;
    private readonly WindowWatcher windows;
    private readonly IFactRecorder facts;

    private ProgramRun(JobRun job, WindowWatcher windows, IFactRecorder facts)
    {
        this.job = job;
        this.windows = windows;
        this.facts = facts;
    }

    public int ProcessId => job.ProcessId;

    /// <inheritdoc cref="JobRun.Start"/>
    public static ProgramRun Start(
        string application,
        string commandLine,
        string? workingDirectory,
        IFactRecorder facts,
        IProgramEvents events,
        TimeSpan? sampleInterval = null,
        TimeSpan? windowInterval = null)
    {
        var job = JobRun.Start(application, commandLine, workingDirectory, facts, events, sampleInterval);
        var windows = new WindowWatcher(job.LiveProcessIds, job.ProcessId, facts, events, windowInterval);
        windows.Start();
        return new ProgramRun(job, windows, facts);
    }

    public IReadOnlyList<DialogInfo> Dialogs() => windows.Dialogs();

    public async Task<ActionResult> PressAsync(string button, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(button);
        var open = windows.Dialogs();
        if (open.Count == 0)
        {
            return ActionResult.Failed(WindowActionFailures.NoDialog);
        }

        var dialog = IntPtr.Zero;
        var target = IntPtr.Zero;
        for (var i = open.Count - 1; i >= 0 && target == IntPtr.Zero; i--)
        {
            dialog = new IntPtr(open[i].Handle);
            target = WindowWatcher.FindButton(dialog, button);
        }
        if (target == IntPtr.Zero)
        {
            return ActionResult.Failed(WindowActionFailures.NoButton);
        }

        var before = Cursor();
        var attempts = 0;
        string result;
        var started = Stopwatch.StartNew();
        while (true)
        {
            attempts++;
            result = await Task.Run(() => UiAutomation.Invoke(target, InvokeTimeout), cancellationToken).ConfigureAwait(false);
            // Не вернувшийся вызов мог нажать: повторять нельзя. Повторяется только сбой, и только если после паузы
            // кнопка на месте: сбой, пришедший вместе с закрытием диалога, — это состоявшееся нажатие.
            if (result is UiAutomation.Invoked or UiAutomation.TimedOut || started.Elapsed >= RetryWindow)
            {
                break;
            }
            await Task.Delay(RetryPause, cancellationToken).ConfigureAwait(false);
            if (!IsWindow(target))
            {
                break;
            }
        }
        var after = Cursor();
        facts.Record(
            ProgramFactKinds.DialogPressed,
            new DialogPressed(dialog.ToInt64(), target.ToInt64(), button, "uia-invoke", result, attempts, !Equals(before, after)),
            job.ProcessId);
        // Кнопка исчезла вместе с диалогом между попытками — нажатие состоялось, дальше проверяется закрытие.
        if (result != UiAutomation.Invoked && IsWindow(target))
        {
            return ActionResult.Failed(WindowActionFailures.PressFailed);
        }

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(CloseTimeout);
        try
        {
            while (IsWindow(dialog) && IsWindowVisible(dialog))
            {
                await Task.Delay(50, limit.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ActionResult.Failed(WindowActionFailures.NotClosed);
        }
        return ActionResult.Done;
    }

    public ActionResult Close()
    {
        var main = windows.MainWindow;
        if (main == IntPtr.Zero || !IsWindow(main))
        {
            return ActionResult.Failed(WindowActionFailures.NoWindow);
        }
        // Сообщение в очередь, как у крестика: программа может спросить о сохранении, и ответ ждёт сценарий.
        PostMessageW(main, WmClose, IntPtr.Zero, IntPtr.Zero);
        facts.Record(ProgramFactKinds.CloseRequested, new CloseRequested(main.ToInt64()), job.ProcessId);
        return ActionResult.Done;
    }

    /// <summary>Прекращает наблюдение. Программа, если жива, продолжает работать.</summary>
    public void Dispose()
    {
        windows.Dispose();
        job.Dispose();
    }
}
