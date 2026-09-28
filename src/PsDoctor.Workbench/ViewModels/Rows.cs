using System.Globalization;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using PsDoctor.Core.Observation;
using PsDoctor.Core.Scenarios;

namespace PsDoctor.Workbench.ViewModels;

/// <summary>Строка ленты фактов. Факт показывается как он лежит в журнале: вид именем, данные — JSON.</summary>
public sealed class FactRow
{
    public FactRow(Fact fact)
    {
        ArgumentNullException.ThrowIfNull(fact);
        Number = fact.Number;
        Kind = fact.Kind;
        ProcessId = fact.ProcessId;
        Elapsed = fact.Elapsed.ToString(@"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture);
        Data = fact.Data.ValueKind == JsonValueKind.Object && fact.Data.EnumerateObject().Any()
            ? fact.Data.GetRawText()
            : "";
    }

    public long Number { get; }

    public string Elapsed { get; }

    public string Kind { get; }

    public int? ProcessId { get; }

    public string Data { get; }

    /// <summary>Данные целиком, с отступами — для панели выбранного факта; строке ленты хватает одной строки.</summary>
    public string Details
    {
        get
        {
            if (Data.Length == 0) return "";
            using var document = JsonDocument.Parse(Data);
            return JsonSerializer.Serialize(document.RootElement, IndentedJson);
        }
    }

    /// <summary>Веха — факт о ходе опыта, а не отсчёт телеметрии: её видно во вкладке «Журнал» по умолчанию.</summary>
    public bool IsMilestone => Milestones.Contains(Kind);

    /// <summary>
    /// Сценарий, оператор, диалоги, главное окно, программа и сеанс. Процессы, отсчёты и файловый ввод-вывод сюда не
    /// входят: за рендер их тысячи, и в ленте из вех они вытеснили бы то, ради чего её смотрят.
    /// </summary>
    public static IReadOnlySet<string> Milestones { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        ProgramFactKinds.SessionStarted,
        ProgramFactKinds.SessionFinished,
        ProgramFactKinds.Environment,
        ProgramFactKinds.ProgramLaunched,
        ProgramFactKinds.ProgramAttached,
        ProgramFactKinds.LaunchFailed,
        ProgramFactKinds.EtwState,
        ProgramFactKinds.MainWindow,
        ProgramFactKinds.DialogOpened,
        ProgramFactKinds.DialogClosed,
        ProgramFactKinds.DialogPressed,
        ProgramFactKinds.CloseRequested,
        ProgramFactKinds.RenderRequested,
        ProgramFactKinds.RenderArtifacts,
        ProgramFactKinds.Episode,
        ProgramFactKinds.Incident,
        ScenarioFactKinds.ScenarioStarted,
        ScenarioFactKinds.StepStarted,
        ScenarioFactKinds.StepDone,
        ScenarioFactKinds.StepFailed,
        ScenarioFactKinds.UnexpectedDialog,
        ScenarioFactKinds.OperatorInstruction,
        ScenarioFactKinds.OperatorConfirmed,
        ScenarioFactKinds.ScenarioFinished,
    };

    private static readonly JsonSerializerOptions IndentedJson = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}

/// <summary>Цвет лампы: смысл, а не оттенок — оттенок назначает окно.</summary>
public enum LampTone
{
    /// <summary>Выключено: не подключён, не запущено, не идёт.</summary>
    Off,

    /// <summary>Работает как надо: на связи, под наблюдением, прогон идёт.</summary>
    On,

    /// <summary>Требует внимания человека: ProShow мимо наблюдателя, прогон сорвался.</summary>
    Attention,

    /// <summary>Беда: нет связи с наблюдателем.</summary>
    Alarm,
}

/// <summary>Лампа в полосе состояния: подпись (окно даёт её подсказкой), что горит словами, и цвет.</summary>
public sealed record Lamp(string Label, string Text, LampTone Tone)
{
    // Окну нужны признаки, а не перечисление: стиль вешается на класс по булеву признаку без конвертера.
    public bool IsOn => Tone == LampTone.On;

    public bool IsAttention => Tone == LampTone.Attention;

    public bool IsAlarm => Tone == LampTone.Alarm;

    /// <summary>Подсказка: подпись и полный текст — в полосе текст бывает обрезан.</summary>
    public string Tip => $"{Label}: {Text}";
}

/// <summary>Главная кнопка панели «Сейчас»: одно действие, которого обстановка ждёт первым.</summary>
public enum PrimaryAction
{
    /// <summary>Делать нечего: опыт не выбран.</summary>
    None,

    Connect,

    Confirm,

    Cancel,

    Export,

    Run,
}

/// <summary>Состояние шага сценария в пульте: его назначают факты исполнителя, а не пульт.</summary>
public enum StepState
{
    /// <summary>Шаг ещё не начинали.</summary>
    Waiting,

    Running,

    Done,

    Failed,
}

/// <summary>Строка списка шагов. Сами шаги пульт берёт из своего разбора текста, состояние — из фактов.</summary>
public sealed class StepRow(int line, string text) : ObservableObject
{
    private StepState state = StepState.Waiting;
    private string note = "";

    public int Line { get; } = line;

    public string Text { get; } = text;

    public StepState State
    {
        get => state;
        set
        {
            if (SetProperty(ref state, value))
            {
                OnPropertyChanged(nameof(IsCurrent));
            }
        }
    }

    /// <summary>Шаг, на котором стоит сценарий: окно выделяет его строку.</summary>
    public bool IsCurrent => State == StepState.Running;

    /// <summary>Что пришло с фактом шага: секунды у выполненного, устойчивое имя причины у сорвавшегося.</summary>
    public string Note
    {
        get => note;
        set => SetProperty(ref note, value);
    }
}

/// <summary>Строка списка сеансов.</summary>
public sealed class SessionRow(SessionSummary summary)
{
    public string Id { get; } = summary.Id;

    public bool Active { get; } = summary.Active;

    public long? LastNumber { get; } = summary.LastNumber;

    public bool Finished { get; } = summary.Finished;

    /// <summary>Как сеанс подписан в списке: живой, доведённый до конца или оборванный.</summary>
    public string Mark => Active ? "живой" : Finished ? "закрыт" : "оборван";
}

/// <summary>
/// Открытый диалог программы и его кнопки: каждую можно нажать из пульта. Кнопки без имени — вкладки и значки
/// вроде тех, что в Slide Options, — не показываются: нажать их по имени нельзя, а пустой квадрат только путает.
/// </summary>
public sealed class DialogRow(DialogInfo dialog)
{
    public long Handle { get; } = dialog.Handle;

    public string Title { get; } = dialog.Title ?? "";

    public string Text { get; } = string.Join(" | ", dialog.Texts);

    public IReadOnlyList<string> Buttons { get; } = [.. dialog.Buttons.Where(b => !string.IsNullOrWhiteSpace(b))];
}

/// <summary>Сценарий опыта — файл рядом с пультом.</summary>
public sealed record ExperimentRow(string Name, string Path);

/// <summary>Пресет машины — файл в каталоге пресетов, по имени файла.</summary>
public sealed record PresetRow(string Name, string Path);
