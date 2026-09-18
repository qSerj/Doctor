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
        set => SetProperty(ref state, value);
    }

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

/// <summary>Открытый диалог программы и его кнопки: каждую можно нажать из пульта.</summary>
public sealed class DialogRow(DialogInfo dialog)
{
    public long Handle { get; } = dialog.Handle;

    public string Title { get; } = dialog.Title ?? "";

    public string Text { get; } = string.Join(" | ", dialog.Texts);

    public IReadOnlyList<string> Buttons { get; } = dialog.Buttons;
}
