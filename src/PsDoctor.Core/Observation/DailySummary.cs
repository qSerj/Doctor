using System.Globalization;
using System.Text.Json;

namespace PsDoctor.Core.Observation;

/// <summary>
/// Краткая запись сеанса (Э6.6): то, что дневной сводке нужно от журнала, без самого журнала. Наблюдатель пишет её при
/// завершении сеанса; у старого или оборванного сеанса она один раз собирается из журнала тем же
/// <see cref="FromFacts"/> — сводка не перечитывает гигабайтные журналы каждый день.
/// </summary>
/// <param name="Ended">Время последнего факта: у оборванного сеанса — последнее, что успели записать.</param>
/// <param name="EndReason">Причина из <c>session-finished</c>, <see cref="SessionEndReasons"/>; <c>null</c> — сеанс оборван или ещё идёт.</param>
/// <param name="Incidents">Фактов <c>incident</c> — меток, легших в сеанс.</param>
/// <param name="Episodes">Паттерны эпизодов детекторов по порядку фактов.</param>
/// <param name="EtwFailures">Отказов записи ETW: <c>etw-state</c> с состоянием <c>failed</c> или <c>unavailable</c>.</param>
public sealed record SessionBrief(
    int Schema,
    string Id,
    DateTimeOffset Started,
    DateTimeOffset Ended,
    string? Origin,
    string? EndReason,
    int Incidents,
    IReadOnlyList<string> Episodes,
    int EtwFailures)
{
    public const int CurrentSchema = 1;

    /// <summary>Краткая запись по фактам журнала; <c>null</c> — в журнале нет <c>session-started</c> с временем начала.</summary>
    public static SessionBrief? FromFacts(string id, IEnumerable<Fact> facts)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentNullException.ThrowIfNull(facts);
        DateTimeOffset? started = null;
        string? origin = null;
        string? endReason = null;
        var last = TimeSpan.Zero;
        var incidents = 0;
        var etwFailures = 0;
        var episodes = new List<string>();
        foreach (var fact in facts)
        {
            if (fact.Elapsed > last)
            {
                last = fact.Elapsed;
            }
            switch (fact.Kind)
            {
                case ProgramFactKinds.SessionStarted:
                    started = Time(fact.Data, "startedAt");
                    origin = Text(fact.Data, "origin");
                    break;
                case ProgramFactKinds.SessionFinished:
                    endReason = Text(fact.Data, "reason");
                    break;
                case ProgramFactKinds.Incident:
                    incidents++;
                    break;
                case ProgramFactKinds.Episode:
                    episodes.Add(Text(fact.Data, "pattern") ?? "");
                    break;
                case ProgramFactKinds.EtwState when Text(fact.Data, "state") is "failed" or "unavailable":
                    etwFailures++;
                    break;
            }
        }
        return started is { } start
            ? new SessionBrief(CurrentSchema, id, start, start + last, origin, endReason, incidents, episodes, etwFailures)
            : null;
    }

    private static string? Text(JsonElement data, string name) =>
        data.ValueKind == JsonValueKind.Object && data.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static DateTimeOffset? Time(JsonElement data, string name) =>
        Text(data, name) is { } text && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var time)
            ? time
            : null;
}

/// <summary>Свободное место, замеренное в известный миг: последний факт <c>environment</c> дня или ежедневное снятие.</summary>
public sealed record DiskReading(DateTimeOffset Measured, DiskSpace? System, DiskSpace? Temp);

/// <summary>Всё, из чего считается сводка дня. Часов и файлов у сводки нет: что сейчас и что лежит на диске, знает наблюдатель.</summary>
/// <param name="Offset">Смещение часового пояса машины: день — календарные сутки по её местному времени.</param>
/// <param name="Complete">День уже кончился, когда сводку строили; сегодняшняя сводка — неполная.</param>
/// <param name="Sessions">Краткие записи сеансов, задевающих день; лишние отбрасываются.</param>
/// <param name="Events">События журналов Application и System; вне дня отбрасываются.</param>
/// <param name="Incidents">Строки файла меток; вне дня отбрасываются.</param>
/// <param name="ObserverRestarts">Времена строк <c>observer-restarted</c> из журнала сторожа.</param>
/// <param name="Snapshot">Последний слепок окружения, снятый за день.</param>
/// <param name="PreviousSnapshot">Последний слепок до этого дня; <c>null</c> — сравнивать не с чем.</param>
/// <param name="JournalBytes">Объём журналов наблюдателя на диске при построении.</param>
/// <param name="History">Сводки прошлых дней — для «обычного» числа падений воркеров.</param>
/// <param name="EventsCovered">Опрос журналов Windows охватывает весь день: закладка начата раньше его начала.</param>
/// <param name="SessionsCovered">Журналы сеансов за день не удалены хранением.</param>
public sealed record DailySummaryInput(
    DateOnly Day,
    TimeSpan Offset,
    bool Complete,
    IReadOnlyList<SessionBrief> Sessions,
    IReadOnlyList<WindowsEvent> Events,
    IReadOnlyList<IncidentRecord> Incidents,
    IReadOnlyList<DateTimeOffset> ObserverRestarts,
    EnvironmentSnapshot? Snapshot,
    EnvironmentSnapshot? PreviousSnapshot,
    DiskReading? Disks,
    long? JournalBytes,
    IReadOnlyList<DailySummary> History,
    bool EventsCovered = true,
    bool SessionsCovered = true);

/// <summary>Имя и число: строки сводки упорядочены, словарь в JSON порядка бы не держал.</summary>
public sealed record NamedCount(string Name, int Count);

/// <summary>Что в сводке стоит прочесть первым. Слов нет: вид — устойчивое имя, фразу подбирает тот, кто показывает.</summary>
public sealed record SummaryHighlight(string Kind, int Count);

/// <summary>Виды главного в порядке важности.</summary>
public static class SummaryHighlights
{
    /// <summary>Упал сам <c>proshow.exe</c>.</summary>
    public const string ProShowCrash = "proshow-crash";

    /// <summary>Метка инцидента: «Решить проблему» или инженер.</summary>
    public const string Complaint = "complaint";

    /// <summary>Воркеры падали заметно чаще обычного.</summary>
    public const string WorkerSpike = "worker-spike";

    /// <summary>Kernel-Power 41: машина выключилась без завершения работы.</summary>
    public const string UnexpectedShutdown = "unexpected-shutdown";

    /// <summary>Синий экран: отчёт 1001 от WER-SystemErrorReporting или код в Kernel-Power 41.</summary>
    public const string BugCheck = "bugcheck";

    /// <summary>Меньше <see cref="DailySummaries.LowDiskBytes"/> на системном диске или диске <c>%TEMP%</c>.</summary>
    public const string LowDisk = "low-disk";

    /// <summary>Слепок окружения не тот, что накануне.</summary>
    public const string EnvironmentChanged = "environment-changed";

    /// <summary>Сторож перезапускал наблюдатель или запись ETW отказывала.</summary>
    public const string DoctorTrouble = "doctor-trouble";
}

/// <summary>Чего за день не было в данных: сводка говорит об этом, а не показывает ноль.</summary>
public static class SummaryGaps
{
    public const string Events = "events";
    public const string Sessions = "sessions";
    public const string Snapshot = "snapshot";
    public const string Disks = "disks";
}

/// <param name="Hours">Часов под наблюдением внутри дня: сеанс через полночь делится по времени.</param>
/// <param name="ByOrigin">Кем начаты; без <c>origin</c> — пустое имя.</param>
/// <param name="ByEnd">Чем кончились, <see cref="SessionEndReasons"/>; оборванный или идущий — <see cref="DailySummaries.Unfinished"/>.</param>
/// <param name="Episodes">Эпизоды по паттернам.</param>
public sealed record ProShowDay(
    int Sessions,
    double Hours,
    IReadOnlyList<NamedCount> ByOrigin,
    IReadOnlyList<NamedCount> ByEnd,
    IReadOnlyList<Complaint> Complaints,
    IReadOnlyList<NamedCount> Episodes);

/// <summary>Метка инцидента в сводке.</summary>
public sealed record Complaint(DateTimeOffset At, string Source, string? Note);

/// <summary>Падения и зависания одной программы за день, по событиям 1000 и 1002 журнала Application.</summary>
/// <param name="Image">Имя образа без расширения, строчными.</param>
/// <param name="Signatures">Подписи падений «модуль+смещение» с числом каждой, частые первыми.</param>
/// <param name="Times">Время каждого падения — кроме воркеров: их бывает сотня в день.</param>
public sealed record ProgramCrashes(string Image, int Crashes, int Hangs, IReadOnlyList<NamedCount> Signatures, IReadOnlyList<DateTimeOffset> Times);

/// <summary>Состояние сравнения падений воркеров с обычным.</summary>
public enum SpikeState
{
    No,
    Yes,

    /// <summary>Прошлых полных дней меньше <see cref="DailySummaries.MinHistoryDays"/> — вывода нет.</summary>
    InsufficientHistory,
}

/// <param name="Today">Падений воркеров за день.</param>
/// <param name="Median">Медиана прошлых полных дней; <c>null</c> — истории мало.</param>
/// <param name="HistoryDays">Сколько прошлых дней взято.</param>
public sealed record WorkerSpike(int Today, double? Median, int HistoryDays, SpikeState State);

/// <summary>Kernel-Power 41.</summary>
/// <param name="BugCheck">Код синего экрана, первый параметр; <c>null</c> — синего экрана не было (0).</param>
/// <param name="PowerButton">Седьмой параметр не ноль: перед выключением жали кнопку питания.</param>
public sealed record UnexpectedShutdown(DateTimeOffset At, string? BugCheck, bool PowerButton);

/// <param name="LastAliveReports">EventLog 6008 — последняя отметка «жив» перед неожиданным выключением.</param>
/// <param name="BugCheckReports">1001 от WER-SystemErrorReporting — отчёт о синем экране.</param>
/// <param name="Whea">Записей WHEA-Logger.</param>
/// <param name="Boots">EventLog 6005 — запуск журнала событий, то есть загрузка.</param>
/// <param name="CleanShutdowns">EventLog 6006 — остановка журнала событий при обычном завершении работы.</param>
public sealed record MachineDay(
    IReadOnlyList<UnexpectedShutdown> UnexpectedShutdowns,
    int LastAliveReports,
    int BugCheckReports,
    int Whea,
    int Boots,
    int CleanShutdowns);

/// <summary>Изменения слепка по разделу.</summary>
public sealed record SectionChanges(string Section, int Added, int Removed, int Changed);

/// <summary>Одна изменённая запись слепка; у добавленной и убранной полей нет.</summary>
/// <param name="Change"><c>added</c>, <c>removed</c> или <c>changed</c>.</param>
public sealed record EnvironmentChangeEntry(string Change, string Section, int? View, string Key, IReadOnlyList<EnvironmentFieldChange> Fields);

/// <param name="Snapshot">Слепок дня; <c>null</c> — за день не снимался.</param>
/// <param name="PreviousSnapshot">С чем сравнивали; <c>null</c> — не с чем.</param>
/// <param name="Truncated">Записей больше <see cref="DailySummaries.MaxEnvironmentEntries"/>, показаны первые.</param>
public sealed record EnvironmentDay(
    string? Snapshot,
    string? PreviousSnapshot,
    IReadOnlyList<SectionChanges> Sections,
    IReadOnlyList<EnvironmentChangeEntry> Entries,
    bool Truncated)
{
    public bool Changed => Sections.Count > 0;
}

/// <param name="ObserverRestarts">Перезапусков наблюдателя сторожем.</param>
/// <param name="EtwFailures">Отказов записи ETW в сеансах дня.</param>
/// <param name="JournalBytes">Объём журналов на диске при построении.</param>
public sealed record DoctorDay(int ObserverRestarts, int EtwFailures, long? JournalBytes);

/// <summary>
/// Сводка дня (Э6.6). Только счёт: повторы между днями, связи и что делать — у того, кто читает. Оле не показывается.
/// </summary>
/// <param name="Highlights">Главное в порядке важности; пусто — день спокойный.</param>
/// <param name="Gaps">Чего за день не было в данных, <see cref="SummaryGaps"/>.</param>
/// <param name="Crashes">Программы: <c>proshow</c> первым, затем воркеры, затем прочие — чаще падавшие первыми.</param>
public sealed record DailySummary(
    int Schema,
    DateOnly Day,
    TimeSpan Offset,
    bool Complete,
    IReadOnlyList<SummaryHighlight> Highlights,
    IReadOnlyList<string> Gaps,
    ProShowDay ProShow,
    IReadOnlyList<ProgramCrashes> Crashes,
    WorkerSpike Workers,
    MachineDay Machine,
    EnvironmentDay Environment,
    DiskReading? Disks,
    DoctorDay Doctor)
{
    public const int CurrentSchema = 1;

    public bool Calm => Highlights.Count == 0;
}

public static class DailySummaries
{
    /// <summary>Образ самой программы.</summary>
    public const string ProShowImage = "proshow";

    /// <summary>Воркеры ProShow: их падения идут в сводку числом, а не в телефон (видение, 05.10.2026).</summary>
    public static IReadOnlyList<string> WorkerImages { get; } = ["fvideo", "device-enc", "device-encp"];

    /// <summary>Сеанс без <c>session-finished</c> — оборванный или ещё идущий.</summary>
    public const string Unfinished = "unfinished";

    /// <summary>Порог места — согласован владельцем 04.10.2026 как критичный для телефона.</summary>
    public const long LowDiskBytes = 10L * 1024 * 1024 * 1024;

    /// <summary>
    /// «Обычное» — медиана стольких прошлых полных дней. Числа всплеска назначены, а не измерены (Э6.6): пересматриваются
    /// по первому месяцу сводок у Оли.
    /// </summary>
    public const int HistoryDays = 14;

    /// <summary>Меньше прошлых дней — «истории мало», вывода нет.</summary>
    public const int MinHistoryDays = 7;

    /// <summary>Всплеск — больше медианы во столько раз…</summary>
    public const double SpikeFactor = 2;

    /// <summary>…и не меньше чем на столько больше неё: при медиане 1 три падения — ещё не всплеск.</summary>
    public const int SpikeMargin = 5;

    /// <summary>Сколько изменённых записей слепка показывать.</summary>
    public const int MaxEnvironmentEntries = 50;

    private const string Application = "Application";
    private const string SystemLog = "System";

    public static DailySummary Build(DailySummaryInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var start = new DateTimeOffset(input.Day.ToDateTime(TimeOnly.MinValue), input.Offset);
        var end = start.AddDays(1);
        bool Inside(DateTimeOffset time) => time >= start && time < end;

        var events = input.Events.Where(e => Inside(e.Time)).OrderBy(e => e.Time).ToList();
        var proShow = ProShow(input, start, end);
        var crashes = Crashes(events);
        var workers = Workers(input, crashes);
        var machine = Machine(events);
        var environment = Environment(input.Snapshot, input.PreviousSnapshot);
        var doctor = new DoctorDay(
            input.ObserverRestarts.Count(Inside),
            input.Sessions.Where(s => Overlap(s, start, end) > TimeSpan.Zero || Inside(s.Started)).Sum(s => s.EtwFailures),
            input.JournalBytes);

        var highlights = new List<SummaryHighlight>();
        void Add(string kind, int count)
        {
            if (count > 0)
            {
                highlights.Add(new SummaryHighlight(kind, count));
            }
        }
        Add(SummaryHighlights.ProShowCrash, crashes.FirstOrDefault(c => c.Image == ProShowImage)?.Crashes ?? 0);
        Add(SummaryHighlights.Complaint, proShow.Complaints.Count);
        Add(SummaryHighlights.WorkerSpike, workers.State == SpikeState.Yes ? workers.Today : 0);
        Add(SummaryHighlights.UnexpectedShutdown, machine.UnexpectedShutdowns.Count);
        Add(SummaryHighlights.BugCheck, Math.Max(machine.BugCheckReports, machine.UnexpectedShutdowns.Count(s => s.BugCheck is not null)));
        Add(SummaryHighlights.LowDisk, LowDisks(input.Disks));
        Add(SummaryHighlights.EnvironmentChanged, environment.Sections.Sum(s => s.Added + s.Removed + s.Changed));
        Add(SummaryHighlights.DoctorTrouble, doctor.ObserverRestarts + doctor.EtwFailures);

        var gaps = new List<string>();
        if (!input.EventsCovered)
        {
            gaps.Add(SummaryGaps.Events);
        }
        if (!input.SessionsCovered)
        {
            gaps.Add(SummaryGaps.Sessions);
        }
        if (input.Snapshot is null)
        {
            gaps.Add(SummaryGaps.Snapshot);
        }
        if (input.Disks is null)
        {
            gaps.Add(SummaryGaps.Disks);
        }

        return new DailySummary(DailySummary.CurrentSchema, input.Day, input.Offset, input.Complete, highlights, gaps,
            proShow, crashes, workers, machine, environment, input.Disks, doctor);
    }

    /// <summary>Имя образа из параметра события: без каталога и <c>.exe</c>, строчными.</summary>
    public static string ImageName(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var name = value[(value.LastIndexOfAny(['\\', '/']) + 1)..].Trim();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^4];
        }
        return name.ToLowerInvariant();
    }

    private static TimeSpan Overlap(SessionBrief session, DateTimeOffset start, DateTimeOffset end)
    {
        var from = session.Started > start ? session.Started : start;
        var to = session.Ended < end ? session.Ended : end;
        return to > from ? to - from : TimeSpan.Zero;
    }

    private static ProShowDay ProShow(DailySummaryInput input, DateTimeOffset start, DateTimeOffset end)
    {
        // Сеанс принадлежит дню, если идёт в нём хоть миг; мгновенный сеанс — если начат в нём.
        var sessions = input.Sessions
            .Where(s => Overlap(s, start, end) > TimeSpan.Zero || (s.Started >= start && s.Started < end))
            .ToList();
        var hours = Math.Round(sessions.Sum(s => Overlap(s, start, end).TotalHours), 2);
        var complaints = input.Incidents
            .Select(i => new Complaint(new DateTimeOffset(DateTime.SpecifyKind(i.AtUtc, DateTimeKind.Utc)), i.Source, i.Note))
            .Where(c => c.At >= start && c.At < end)
            .OrderBy(c => c.At)
            .ToList();
        return new ProShowDay(
            sessions.Count,
            hours,
            Count(sessions.Select(s => s.Origin ?? "")),
            Count(sessions.Select(s => s.EndReason ?? Unfinished)),
            complaints,
            Count(sessions.SelectMany(s => s.Episodes)));
    }

    private static IReadOnlyList<ProgramCrashes> Crashes(IReadOnlyList<WindowsEvent> events)
    {
        var byImage = new Dictionary<string, (int Crashes, int Hangs, List<string> Signatures, List<DateTimeOffset> Times)>();
        foreach (var e in events.Where(x => x.Log == Application && x.Id is 1000 or 1002 && x.Properties.Count > 0))
        {
            var image = ImageName(e.Properties[0]);
            if (!byImage.TryGetValue(image, out var entry))
            {
                entry = (0, 0, new List<string>(), new List<DateTimeOffset>());
            }
            if (e.Id == 1000)
            {
                entry.Crashes++;
                entry.Signatures.Add(Signature(e.Properties));
                if (!WorkerImages.Contains(image))
                {
                    entry.Times.Add(e.Time);
                }
            }
            else
            {
                entry.Hangs++;
            }
            byImage[image] = entry;
        }
        return byImage
            .OrderBy(pair => pair.Key == ProShowImage ? 0 : WorkerImages.Contains(pair.Key) ? 1 : 2)
            .ThenByDescending(pair => pair.Value.Crashes + pair.Value.Hangs)
            .ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new ProgramCrashes(pair.Key, pair.Value.Crashes, pair.Value.Hangs, Count(pair.Value.Signatures), pair.Value.Times))
            .ToList();
    }

    /// <summary>Подпись падения по событию 1000: модуль — четвёртый параметр, смещение — восьмой.</summary>
    private static string Signature(IReadOnlyList<string> properties)
    {
        var module = properties.Count > 3 ? properties[3] : "?";
        var offset = properties.Count > 7 ? properties[7] : "?";
        return $"{module}+{offset}";
    }

    private static WorkerSpike Workers(DailySummaryInput input, IReadOnlyList<ProgramCrashes> crashes)
    {
        var today = crashes.Where(c => WorkerImages.Contains(c.Image)).Sum(c => c.Crashes);
        var past = input.History
            .Where(s => s.Complete && s.Day < input.Day)
            .OrderByDescending(s => s.Day)
            .Take(HistoryDays)
            .Select(s => s.Workers.Today)
            .Order()
            .ToList();
        if (past.Count < MinHistoryDays)
        {
            return new WorkerSpike(today, null, past.Count, SpikeState.InsufficientHistory);
        }
        var median = past.Count % 2 == 1 ? past[past.Count / 2] : (past[past.Count / 2 - 1] + past[past.Count / 2]) / 2.0;
        var spike = today > SpikeFactor * median && today >= median + SpikeMargin;
        return new WorkerSpike(today, median, past.Count, spike ? SpikeState.Yes : SpikeState.No);
    }

    private static MachineDay Machine(IReadOnlyList<WindowsEvent> events)
    {
        var system = events.Where(e => e.Log == SystemLog).ToList();
        var shutdowns = system
            .Where(e => e.Provider == "Microsoft-Windows-Kernel-Power" && e.Id == 41)
            .Select(e => new UnexpectedShutdown(e.Time, NonZero(e.Properties, 0), NonZero(e.Properties, 6) is not null))
            .ToList();
        int CountOf(string provider, int? id = null) => system.Count(e => e.Provider == provider && (id is null || e.Id == id));
        return new MachineDay(
            shutdowns,
            CountOf("EventLog", 6008),
            CountOf("Microsoft-Windows-WER-SystemErrorReporting", 1001),
            CountOf("Microsoft-Windows-WHEA-Logger"),
            CountOf("EventLog", 6005),
            CountOf("EventLog", 6006));
    }

    /// <summary>Параметр, если он есть и не ноль: у Kernel-Power 41 ноль значит «не было».</summary>
    private static string? NonZero(IReadOnlyList<string> properties, int index) =>
        properties.Count > index && properties[index] is { } value && value.Trim() is { Length: > 0 } text
            && !(long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) && number == 0)
            ? text
            : null;

    private static EnvironmentDay Environment(EnvironmentSnapshot? snapshot, EnvironmentSnapshot? previous)
    {
        if (snapshot is null || previous is null || snapshot.Id == previous.Id)
        {
            return new EnvironmentDay(snapshot?.Id, previous?.Id, [], [], false);
        }
        var diff = EnvironmentComparison.Compare(previous, snapshot);
        var entries = diff.Added.Select(e => new EnvironmentChangeEntry("added", e.Section, e.View, e.Key, []))
            .Concat(diff.Removed.Select(e => new EnvironmentChangeEntry("removed", e.Section, e.View, e.Key, [])))
            .Concat(diff.Changed.Select(e => new EnvironmentChangeEntry("changed", e.Section, e.View, e.Key, e.Fields)))
            .ToList();
        var sections = entries
            .GroupBy(e => e.Section)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new SectionChanges(g.Key, g.Count(e => e.Change == "added"), g.Count(e => e.Change == "removed"), g.Count(e => e.Change == "changed")))
            .ToList();
        return new EnvironmentDay(snapshot.Id, previous.Id, sections, entries.Take(MaxEnvironmentEntries).ToList(),
            entries.Count > MaxEnvironmentEntries);
    }

    private static int LowDisks(DiskReading? disks) =>
        disks is null
            ? 0
            : new[] { disks.System, disks.Temp }
                .Where(d => d is not null && d.FreeBytes < LowDiskBytes)
                .Select(d => d!.Root.ToUpperInvariant())
                .Distinct()
                .Count();

    private static IReadOnlyList<NamedCount> Count(IEnumerable<string> names) =>
        names.GroupBy(name => name, StringComparer.Ordinal)
            .Select(g => new NamedCount(g.Key, g.Count()))
            .OrderByDescending(c => c.Count)
            .ThenBy(c => c.Name, StringComparer.Ordinal)
            .ToList();
}

/// <summary>Сводки и краткие записи в файлах — те же правила JSON, что у журнала, с отступами: их читают глазами.</summary>
public static class DailySummaryJson
{
    private static readonly JsonSerializerOptions Indented = new(ObservationJson.Options) { WriteIndented = true };

    public static string Serialize(DailySummary summary) => JsonSerializer.Serialize(summary, Indented);

    public static DailySummary Deserialize(string json) =>
        JsonSerializer.Deserialize<DailySummary>(json, ObservationJson.Options) ?? throw new JsonException("пустая сводка");

    public static string Serialize(SessionBrief brief) => JsonSerializer.Serialize(brief, Indented);

    public static SessionBrief DeserializeBrief(string json) =>
        JsonSerializer.Deserialize<SessionBrief>(json, ObservationJson.Options) ?? throw new JsonException("пустая краткая запись");
}
