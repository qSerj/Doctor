using System.Runtime.Versioning;
using PsDoctor.Core.Observation;
using PsDoctor.Core.Scenarios;
using static PsDoctor.Infrastructure.Observation.Win32Windows;

namespace PsDoctor.Infrastructure.Observation;

/// <summary>
/// Окна куста раз в полсекунды: главное окно и диалоги как факты и события. Читает окна, ничего в них не шлёт.
/// </summary>
/// <remarks>
/// <para>
/// Главное окно — видимое окно основного процесса без владельца и с заголовком; факт — на смену хэндла,
/// заголовка или признака «не отвечает». Диалог — видимое окно любого процесса куста с владельцем и классом
/// из <see cref="DialogClasses"/>; факт — на появление и исчезновение. Прочие окна с владельцем пишутся
/// отдельным видом факта и сигналом сценарию не становятся: неизвестное окно не должно останавливать сценарий,
/// но должно быть видно.
/// </para>
/// <para>
/// Опросу нужен рабочий стол: наблюдатель живёт в сеансе пользователя. В нулевом сеансе окон программы не видно.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowWatcher : IDisposable
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>Классы диалогов ProShow: системный диалог и окно сообщений самой программы.</summary>
    public static readonly IReadOnlySet<string> DialogClasses = new HashSet<string>(StringComparer.Ordinal) { "#32770", "AGDSDocParent" };

    // Сколько опросов ждать кнопок у нового диалога: окно становится видимым раньше, чем в нём появляются кнопки.
    private const int PollsForButtons = 3;

    private readonly Func<IReadOnlyCollection<int>> processes;
    private readonly int mainProcessId;
    private readonly IFactRecorder facts;
    private readonly IProgramEvents events;
    private readonly TimeSpan interval;
    private readonly Lock gate = new();
    // Открытые диалоги в порядке появления: нажатие ищет кнопку с новейшего.
    private readonly List<DialogInfo> dialogs = [];
    private readonly Dictionary<IntPtr, string> others = [];
    private readonly Dictionary<IntPtr, int> pending = [];
    private readonly ManualResetEventSlim stop = new();
    private readonly Thread loop;
    private MainWindowState main = new(null, null, false);
    private bool disposed;

    public WindowWatcher(Func<IReadOnlyCollection<int>> processes, int mainProcessId, IFactRecorder facts, IProgramEvents events, TimeSpan? interval = null)
    {
        ArgumentNullException.ThrowIfNull(processes);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(events);
        this.processes = processes;
        this.mainProcessId = mainProcessId;
        this.facts = facts;
        this.events = events;
        this.interval = interval ?? DefaultInterval;
        loop = new Thread(Run) { IsBackground = true, Name = "psdoctor-windows" };
    }

    public void Start() => loop.Start();

    /// <summary>Хэндл главного окна по последнему опросу.</summary>
    public IntPtr MainWindow
    {
        get
        {
            lock (gate)
            {
                return main.Handle is { } handle ? new IntPtr(handle) : IntPtr.Zero;
            }
        }
    }

    /// <summary>Открытые диалоги, о которых записан факт, — старшие первыми.</summary>
    public IReadOnlyList<DialogInfo> Dialogs()
    {
        lock (gate)
        {
            return [.. dialogs];
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
        }
        stop.Set();
        if (Thread.CurrentThread != loop && loop.IsAlive)
        {
            loop.Join(TimeSpan.FromSeconds(5));
        }
        stop.Dispose();
    }

    private void Run()
    {
        try
        {
            do
            {
                Poll();
            }
            while (!stop.Wait(interval));
        }
        catch (InvalidOperationException)
        {
            // Журнал сеанса закрыт между опросами — писать некуда; или опрос пережил остановку.
        }
    }

    private void Poll()
    {
        var kin = processes();
        var mainHandle = IntPtr.Zero;
        string? mainTitle = null;
        var owned = new List<(IntPtr Hwnd, int ProcessId)>();
        foreach (var hwnd in TopLevelWindows())
        {
            GetWindowThreadProcessId(hwnd, out var processId);
            if (!kin.Contains(processId) || !IsWindowVisible(hwnd))
            {
                continue;
            }
            if (GetWindow(hwnd, GwOwner) != IntPtr.Zero)
            {
                owned.Add((hwnd, processId));
            }
            else if (processId == mainProcessId && mainHandle == IntPtr.Zero && Text(hwnd) is { Length: > 0 } title)
            {
                mainHandle = hwnd;
                mainTitle = title;
            }
        }

        var state = mainHandle == IntPtr.Zero
            ? new MainWindowState(null, null, false)
            : new MainWindowState(mainHandle.ToInt64(), mainTitle, IsHungAppWindow(mainHandle));
        MainWindowState previous;
        lock (gate)
        {
            previous = main;
            main = state;
        }
        if (state != previous)
        {
            facts.Record(ProgramFactKinds.MainWindow, state, mainProcessId);
            if (state.Title != previous.Title)
            {
                events.TitleChanged(state.Title);
            }
        }

        var present = owned.Select(w => w.Hwnd).ToHashSet();
        foreach (var (hwnd, processId) in owned)
        {
            if (IsDialog(hwnd))
            {
                continue;
            }
            var windowClass = ClassName(hwnd);
            if (!DialogClasses.Contains(windowClass))
            {
                if (!Announced(hwnd))
                {
                    lock (gate)
                    {
                        others[hwnd] = windowClass;
                    }
                    facts.Record(ProgramFactKinds.WindowOpened, new OwnedWindow(hwnd.ToInt64(), windowClass, Text(hwnd)), processId);
                }
                continue;
            }

            var dialog = Describe(hwnd, windowClass, processId);
            if (dialog.Buttons.Count == 0)
            {
                // Окно класса диалога без кнопок — ещё не диалог. Заглушки ProShow («Please Wait» о чтении переходов
                // и о списке форматов) кнопок не получают никогда, и останавливать на них сценарий нечестно: нажимать
                // в них нечего, а исчезают они сами. Такое окно пишется обычным окном и сигналом сценарию не становится.
                var announce = false;
                lock (gate)
                {
                    if (!others.ContainsKey(hwnd))
                    {
                        pending[hwnd] = pending.GetValueOrDefault(hwnd) + 1;
                        if (pending[hwnd] >= PollsForButtons)
                        {
                            pending.Remove(hwnd);
                            others[hwnd] = windowClass;
                            announce = true;
                        }
                    }
                }
                if (announce)
                {
                    facts.Record(ProgramFactKinds.WindowOpened, new OwnedWindow(hwnd.ToInt64(), windowClass, Text(hwnd)), processId);
                }
                continue;
            }
            // Кнопки появились — окно становится диалогом, даже если о нём уже записан факт обычного окна:
            // опрос не бросается ни на одном окне класса диалога.
            lock (gate)
            {
                pending.Remove(hwnd);
                others.Remove(hwnd);
                dialogs.Add(dialog);
            }
            facts.Record(ProgramFactKinds.DialogOpened, dialog, processId);
            events.DialogAppeared(dialog);
        }

        List<IntPtr> goneDialogs;
        List<IntPtr> goneOthers;
        lock (gate)
        {
            goneDialogs = [.. dialogs.Select(d => new IntPtr(d.Handle)).Where(h => !present.Contains(h))];
            goneOthers = [.. others.Keys.Where(h => !present.Contains(h))];
            dialogs.RemoveAll(d => !present.Contains(new IntPtr(d.Handle)));
            foreach (var hwnd in goneOthers)
            {
                others.Remove(hwnd);
            }
            foreach (var hwnd in pending.Keys.Where(h => !present.Contains(h)).ToList())
            {
                pending.Remove(hwnd);
            }
        }
        foreach (var hwnd in goneDialogs)
        {
            facts.Record(ProgramFactKinds.DialogClosed, new WindowClosed(hwnd.ToInt64()));
            events.DialogDisappeared(hwnd.ToInt64());
        }
        foreach (var hwnd in goneOthers)
        {
            facts.Record(ProgramFactKinds.WindowClosed, new WindowClosed(hwnd.ToInt64()));
        }
    }

    private bool IsDialog(IntPtr hwnd)
    {
        lock (gate)
        {
            return dialogs.Exists(dialog => dialog.Handle == hwnd.ToInt64());
        }
    }

    /// <summary>Об окне уже записан факт обычного окна.</summary>
    private bool Announced(IntPtr hwnd)
    {
        lock (gate)
        {
            return others.ContainsKey(hwnd);
        }
    }

    /// <summary>Тексты — дочерние <c>Static</c>, кнопки — дочерние <c>Button</c>. У окон, где текст нарисован программой, текстов нет.</summary>
    internal static DialogInfo Describe(IntPtr hwnd, string windowClass, int processId)
    {
        var texts = new List<string>();
        var buttons = new List<string>();
        foreach (var child in VisibleChildren(hwnd))
        {
            switch (ClassName(child))
            {
                case "Static" when Text(child) is { Length: > 0 } text:
                    texts.Add(text);
                    break;
                case "Button":
                    buttons.Add(Text(child));
                    break;
            }
        }
        return new DialogInfo(hwnd.ToInt64(), Text(hwnd), texts, buttons, windowClass, processId);
    }

    /// <summary>Хэндл кнопки с таким текстом; амперсанд подсказки клавиши и краевые пробелы не в счёт.</summary>
    internal static IntPtr FindButton(IntPtr dialog, string text)
    {
        var wanted = Normalize(text);
        foreach (var child in VisibleChildren(dialog))
        {
            if (ClassName(child) == "Button" && Normalize(Text(child)) == wanted)
            {
                return child;
            }
        }
        return IntPtr.Zero;
    }

    internal static string Normalize(string caption) => caption.Replace("&", "", StringComparison.Ordinal).Trim();
}
