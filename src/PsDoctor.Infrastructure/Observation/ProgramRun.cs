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

    /// <summary>Сколько ждать каждого окна на пути рендера: список форматов программа сперва ищет в сети.</summary>
    public static readonly TimeSpan RenderStepTimeout = TimeSpan.FromSeconds(120);

    private readonly JobRun job;
    private readonly WindowWatcher windows;
    private readonly IFactRecorder facts;
    private readonly SessionHygiene? hygiene;
    private bool aliveAtStop;

    private ProgramRun(JobRun job, WindowWatcher windows, IFactRecorder facts, SessionHygiene? hygiene)
    {
        this.job = job;
        this.windows = windows;
        this.facts = facts;
        this.hygiene = hygiene;
    }

    public int ProcessId => job.ProcessId;

    /// <inheritdoc cref="JobRun.Start"/>
    /// <param name="hygiene">Гигиена сеанса: снимок «до» снимается здесь, перед запуском; <c>null</c> — без неё.</param>
    public static ProgramRun Start(
        string application,
        string commandLine,
        string? workingDirectory,
        IFactRecorder facts,
        IProgramEvents events,
        TimeSpan? sampleInterval = null,
        TimeSpan? windowInterval = null,
        SessionHygiene? hygiene = null)
    {
        hygiene?.Begin();
        var job = JobRun.Start(application, commandLine, workingDirectory, facts, events, sampleInterval);
        var windows = new WindowWatcher(job.LiveProcessIds, job.ProcessId, facts, events, windowInterval);
        windows.Start();
        return new ProgramRun(job, windows, facts, hygiene);
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

        for (var i = open.Count - 1; i >= 0; i--)
        {
            var dialog = new IntPtr(open[i].Handle);
            if (WindowWatcher.FindButton(dialog, button) is { } target && target != IntPtr.Zero)
            {
                return await PressButtonAsync(dialog, target, button, cancellationToken).ConfigureAwait(false);
            }
        }
        return ActionResult.Failed(WindowActionFailures.NoButton);
    }

    /// <summary>Нажатие найденной кнопки: Invoke с повтором при сбое и ожидание, пока окно закроется.</summary>
    private async Task<ActionResult> PressButtonAsync(IntPtr dialog, IntPtr target, string button, CancellationToken cancellationToken)
    {
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

    /// <summary>
    /// Путь рендера, снятый на стенде 17.09.2026: команда меню главному окну открывает окно вывода без открытия
    /// самого меню (меню программа рисует сама, и мышь для него не нужна), «Create» ведёт к системному окну
    /// сохранения, где имя файла и каталог уже умолчальные — каталог проекта. Действие возвращается, когда встало
    /// окно рендера; окончания оно не ждёт.
    /// </summary>
    public async Task<ActionResult> RenderAsync(CancellationToken cancellationToken)
    {
        var main = windows.MainWindow;
        if (main == IntPtr.Zero || !IsWindow(main))
        {
            return ActionResult.Failed(WindowActionFailures.NoWindow);
        }

        PostMessageW(main, WmCommand, new IntPtr(ProShowWindows.PublishVideoCommand), IntPtr.Zero);
        facts.Record(
            ProgramFactKinds.RenderRequested,
            new RenderRequested(main.ToInt64(), ProShowWindows.PublishVideoCommand),
            job.ProcessId);

        if (await AwaitDialogAsync(ProShowWindows.OutputWindow, cancellationToken).ConfigureAwait(false) is not { } output)
        {
            return ActionResult.Failed(WindowActionFailures.NoOutputWindow);
        }
        var create = WindowWatcher.FindButton(new IntPtr(output.Handle), ProShowWindows.CreateButton);
        if (create == IntPtr.Zero)
        {
            return ActionResult.Failed(WindowActionFailures.NoButton);
        }
        var created = await PressButtonAsync(new IntPtr(output.Handle), create, ProShowWindows.CreateButton, cancellationToken).ConfigureAwait(false);
        if (!created.Succeeded)
        {
            return created;
        }

        if (await AwaitDialogAsync(ProShowWindows.SaveDialog, cancellationToken).ConfigureAwait(false) is not { } save)
        {
            return ActionResult.Failed(WindowActionFailures.NoSaveDialog);
        }
        // Кнопку согласия системного окна ищем по id, а не по тексту: он переводится языком системы.
        var accept = GetDlgItem(new IntPtr(save.Handle), IdOk);
        if (accept == IntPtr.Zero)
        {
            return ActionResult.Failed(WindowActionFailures.NoButton);
        }
        var accepted = await PressButtonAsync(new IntPtr(save.Handle), accept, Text(accept), cancellationToken).ConfigureAwait(false);
        if (!accepted.Succeeded)
        {
            return accepted;
        }

        return await AwaitDialogAsync(ProShowWindows.RenderingWindow, cancellationToken).ConfigureAwait(false) is null
            ? ActionResult.Failed(WindowActionFailures.NoRenderWindow)
            : ActionResult.Done;
    }

    /// <summary>Ждёт открытый диалог с таким заголовком; <c>null</c> — не дождался за <see cref="RenderStepTimeout"/>.</summary>
    private async Task<DialogInfo?> AwaitDialogAsync(string title, CancellationToken cancellationToken)
    {
        var waiting = Stopwatch.StartNew();
        while (true)
        {
            if (windows.Dialogs().LastOrDefault(dialog => dialog.Title == title) is { } found)
            {
                return found;
            }
            if (waiting.Elapsed >= RenderStepTimeout)
            {
                return null;
            }
            await Task.Delay(RetryPause, cancellationToken).ConfigureAwait(false);
        }
    }

    public void Conclude() => hygiene?.Conclude(aliveAtStop);

    /// <summary>Прекращает наблюдение. Программа, если жива, продолжает работать.</summary>
    public void Dispose()
    {
        windows.Dispose();
        aliveAtStop = job.LiveProcessIds().Count > 0;
        job.Dispose();
    }
}
