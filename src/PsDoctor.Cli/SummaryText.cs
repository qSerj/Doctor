using System.Globalization;
using PsDoctor.Core.Observation;

namespace PsDoctor.Cli;

/// <summary>
/// Сводка дня (Э6.6) словами — для владельца и агента на осмотре: день на экран, главное первой строкой. Слова живут
/// здесь, в ядре — только устойчивые имена. Время — местное время машины, на которой сводка посчитана.
/// </summary>
public static class SummaryText
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static void Write(DailySummary summary, TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteLine($"{summary.Day.ToString("yyyy-MM-dd", Invariant)} {(summary.Complete ? "— день закончен" : "— день идёт, сводка неполная")}");
        writer.WriteLine(summary.Calm
            ? "Главное: день спокойный."
            : "Главное: " + string.Join("; ", summary.Highlights.Select(h => Highlight(h, summary))) + ".");
        if (summary.Gaps.Count > 0)
        {
            writer.WriteLine("Нет в данных: " + string.Join("; ", summary.Gaps.Select(Gap)) + ".");
        }

        var proShow = summary.ProShow;
        writer.WriteLine(proShow.Sessions == 0
            ? "ProShow: сеансов не было."
            : $"ProShow: сеансов {Number(proShow.Sessions)}, под наблюдением {Fraction(proShow.Hours)} ч"
                + $" (начаты: {Counts(proShow.ByOrigin, name => name.Length == 0 ? "без метки" : name)};"
                + $" кончились: {Counts(proShow.ByEnd, name => name == DailySummaries.Unfinished ? "оборван или идёт" : name)})"
                + (proShow.Episodes.Count > 0 ? $"; эпизоды: {Counts(proShow.Episodes, name => name)}" : "") + ".");
        foreach (var complaint in proShow.Complaints)
        {
            writer.WriteLine($"  жалоба {Time(complaint.At, summary)} {complaint.Source}" + (complaint.Note is { } note ? $" «{note}»" : ""));
        }

        if (summary.Crashes.Count == 0)
        {
            writer.WriteLine("Падения программ: нет.");
        }
        else
        {
            writer.WriteLine("Падения программ:");
            foreach (var program in summary.Crashes)
            {
                var times = program.Times.Count > 0 ? " — " + string.Join(", ", program.Times.Select(t => Time(t, summary))) : "";
                writer.WriteLine($"  {program.Image}: падений {Number(program.Crashes)}, зависаний {Number(program.Hangs)}{times}");
                if (program.Signatures.Count > 0)
                {
                    writer.WriteLine("    " + Counts(program.Signatures, name => name));
                }
            }
        }

        var workers = summary.Workers;
        writer.WriteLine(workers.State switch
        {
            SpikeState.InsufficientHistory => $"Воркеры: падений {Number(workers.Today)}; истории мало — {Number(workers.HistoryDays)} дн.",
            SpikeState.Yes => $"Воркеры: падений {Number(workers.Today)} при обычных {Fraction(workers.Median ?? 0)} за {Number(workers.HistoryDays)} дн. — всплеск.",
            _ => $"Воркеры: падений {Number(workers.Today)} при обычных {Fraction(workers.Median ?? 0)} за {Number(workers.HistoryDays)} дн.",
        });

        var machine = summary.Machine;
        var shutdowns = machine.UnexpectedShutdowns.Count == 0
            ? "аварийных выключений нет"
            : "аварийные выключения: " + string.Join(", ", machine.UnexpectedShutdowns.Select(s => Time(s.At, summary)
                + (s.BugCheck is { } code ? $" синий экран {code}" : "")
                + (s.PowerButton ? " кнопка питания" : "")));
        writer.WriteLine($"Машина: {shutdowns}; отметок 6008 — {Number(machine.LastAliveReports)}, отчётов о синем экране — "
            + $"{Number(machine.BugCheckReports)}, WHEA — {Number(machine.Whea)}; загрузок {Number(machine.Boots)}, "
            + $"обычных завершений {Number(machine.CleanShutdowns)}.");

        var environment = summary.Environment;
        if (environment.Snapshot is null)
        {
            writer.WriteLine("Окружение: слепка за день нет.");
        }
        else if (!environment.Changed)
        {
            writer.WriteLine($"Окружение: слепок {environment.Snapshot}"
                + (environment.PreviousSnapshot is null ? ", сравнить не с чем." : ", без изменений."));
        }
        else
        {
            writer.WriteLine($"Окружение: {environment.PreviousSnapshot} → {environment.Snapshot}: "
                + string.Join(", ", environment.Sections.Select(s => $"{s.Section} +{Number(s.Added)} −{Number(s.Removed)} ~{Number(s.Changed)}"))
                + (environment.Truncated ? $" (показаны первые {Number(environment.Entries.Count)})" : ""));
            foreach (var entry in environment.Entries)
            {
                var mark = entry.Change switch { "added" => "+", "removed" => "-", _ => "~" };
                var view = entry.View?.ToString(Invariant) ?? "-";
                writer.WriteLine($"  {mark} {entry.Section} {view} {entry.Key}"
                    + string.Concat(entry.Fields.Select(f => $"  {f.Name}: {f.Before ?? "—"} → {f.After ?? "—"}")));
            }
        }

        if (summary.Disks is { } disks)
        {
            writer.WriteLine($"Место ({Time(disks.Measured, summary)}): системный {Disk(disks.System)}; %TEMP% {Disk(disks.Temp)}.");
        }

        var doctor = summary.Doctor;
        writer.WriteLine($"Doctor: перезапусков наблюдателя {Number(doctor.ObserverRestarts)}, отказов ETW {Number(doctor.EtwFailures)}"
            + (doctor.JournalBytes is { } bytes ? $", журналы {Gigabytes(bytes)} ГБ." : "."));
    }

    private static string Highlight(SummaryHighlight highlight, DailySummary summary) => highlight.Kind switch
    {
        SummaryHighlights.ProShowCrash => $"упал ProShow ×{Number(highlight.Count)}",
        SummaryHighlights.Complaint => $"жалоба ×{Number(highlight.Count)}",
        SummaryHighlights.WorkerSpike => $"всплеск падений воркеров: {Number(highlight.Count)} при обычных {Fraction(summary.Workers.Median ?? 0)}",
        SummaryHighlights.UnexpectedShutdown => $"аварийное выключение ×{Number(highlight.Count)}",
        SummaryHighlights.BugCheck => $"синий экран ×{Number(highlight.Count)}",
        SummaryHighlights.LowDisk => $"меньше {Gigabytes(DailySummaries.LowDiskBytes)} ГБ на диске ×{Number(highlight.Count)}",
        SummaryHighlights.EnvironmentChanged => $"окружение изменилось: записей {Number(highlight.Count)}",
        SummaryHighlights.DoctorTrouble => $"сбои Doctor ×{Number(highlight.Count)}",
        _ => $"{highlight.Kind} ×{Number(highlight.Count)}",
    };

    private static string Gap(string gap) => gap switch
    {
        SummaryGaps.Events => "журналы Windows прочитаны не за весь день",
        SummaryGaps.Sessions => "журналы сеансов не за весь день",
        SummaryGaps.Snapshot => "слепок окружения",
        SummaryGaps.Disks => "замер места",
        _ => gap,
    };

    private static string Counts(IEnumerable<NamedCount> counts, Func<string, string> name) =>
        string.Join(", ", counts.Select(c => c.Count == 1 ? name(c.Name) : $"{name(c.Name)} ×{Number(c.Count)}"));

    private static string Disk(DiskSpace? disk) =>
        disk is null ? "не замерен" : $"{disk.Root} свободно {Gigabytes(disk.FreeBytes)} из {Gigabytes(disk.TotalBytes)} ГБ";

    private static string Time(DateTimeOffset time, DailySummary summary) => time.ToOffset(summary.Offset).ToString("HH:mm", Invariant);

    private static string Number(int value) => value.ToString(Invariant);

    /// <summary>Десятичная запятая, как пишут по-русски.</summary>
    private static string Fraction(double value) => value.ToString("0.#", Invariant).Replace('.', ',');

    private static string Gigabytes(long bytes) => Fraction(bytes / (1024.0 * 1024 * 1024));
}
