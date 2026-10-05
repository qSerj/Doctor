using System.Text.Json;
using PsDoctor.Core.Observation;
using Xunit;

namespace PsDoctor.Core.Tests;

public sealed class DailySummaryTests
{
    private static readonly TimeSpan Москва = TimeSpan.FromHours(3);
    private static readonly DateOnly День = new(2026, 10, 5);

    private static DateTimeOffset В(int час, int минута = 0, int деньМесяца = 5) => new(2026, 10, деньМесяца, час, минута, 0, Москва);

    private static WindowsEvent Падение(DateTimeOffset когда, string программа, string модуль = "all.dnt", string смещение = "0x0051b3b5") =>
        new(когда, "Application", "Application Error", 1000, null,
            [программа, "1.0", "0", модуль, "1.0", "0", "0xc0000005", смещение]);

    private static WindowsEvent Зависание(DateTimeOffset когда, string программа) =>
        new(когда, "Application", "Application Hang", 1002, null, [программа, "1.0", "1234"]);

    private static WindowsEvent Системное(DateTimeOffset когда, string поставщик, int код, params string[] параметры) =>
        new(когда, "System", поставщик, код, null, параметры);

    private static WindowsEvent Выключение(DateTimeOffset когда, string синийЭкран = "0", string кнопка = "0") =>
        Системное(когда, "Microsoft-Windows-Kernel-Power", 41, синийЭкран, "0", "0", "0", "0", "0", кнопка);

    private static SessionBrief Сеанс(string id, DateTimeOffset начало, DateTimeOffset конец, string? origin = "watch",
        string? причина = SessionEndReasons.ProgramExited, int меток = 0, string[]? эпизоды = null, int отказовEtw = 0) =>
        new(SessionBrief.CurrentSchema, id, начало, конец, origin, причина, меток, эпизоды ?? [], отказовEtw);

    private static DiskReading Место(long свободноГб) =>
        new(В(12), new DiskSpace(@"C:\", свободноГб * 1024 * 1024 * 1024, 500L * 1024 * 1024 * 1024), new DiskSpace(@"C:\", свободноГб * 1024 * 1024 * 1024, 500L * 1024 * 1024 * 1024));

    private static DailySummaryInput Вход(
        IReadOnlyList<WindowsEvent>? события = null,
        IReadOnlyList<SessionBrief>? сеансы = null,
        IReadOnlyList<IncidentRecord>? метки = null,
        IReadOnlyList<DailySummary>? история = null,
        EnvironmentSnapshot? слепок = null,
        EnvironmentSnapshot? прежний = null,
        DiskReading? место = null,
        IReadOnlyList<DateTimeOffset>? перезапуски = null) =>
        new(День, Москва, Complete: true, сеансы ?? [], события ?? [], метки ?? [], перезапуски ?? [],
            слепок, прежний, место ?? Место(200), 1000, история ?? []);

    private static DailySummary Прошлый(int деньМесяца, int воркеров) =>
        DailySummaries.Build(new DailySummaryInput(new DateOnly(2026, 9, деньМесяца), Москва, true, [],
            Enumerable.Range(0, воркеров).Select(_ => Падение(new DateTimeOffset(2026, 9, деньМесяца, 12, 0, 0, Москва), "fvideo.exe")).ToList(),
            [], [], null, null, null, null, []));

    private static IReadOnlyList<DailySummary> История(params int[] воркеровПоДням) =>
        воркеровПоДням.Select((число, i) => Прошлый(20 + i, число)).ToList();

    [Fact]
    public void Падения_считаются_по_образу_и_подписи_proshow_первым_воркеры_без_времён()
    {
        var сводка = DailySummaries.Build(Вход(
        [
            Падение(В(10), "fvideo.exe", "ntdll.dll", "0x1"),
            Падение(В(11), "fvideo.exe", "ntdll.dll", "0x1"),
            Падение(В(12), "fvideo.exe", "devicef.dll", "0x2"),
            Падение(В(13), "AfterFX.exe", "AfterFX.dll", "0x3"),
            Падение(В(14), @"C:\Program Files (x86)\Photodex\ProShow Producer\proshow.exe"),
            Зависание(В(15), "proshow.exe"),
        ]));

        Assert.Equal(new[] { "proshow", "fvideo", "afterfx" }, сводка.Crashes.Select(c => c.Image));
        var proshow = сводка.Crashes[0];
        Assert.Equal((1, 1), (proshow.Crashes, proshow.Hangs));
        Assert.Equal(new[] { В(14) }, proshow.Times);
        var fvideo = сводка.Crashes[1];
        Assert.Equal(3, fvideo.Crashes);
        Assert.Equal(new[] { new NamedCount("ntdll.dll+0x1", 2), new NamedCount("devicef.dll+0x2", 1) }, fvideo.Signatures);
        Assert.Empty(fvideo.Times);
        Assert.Equal(new[] { В(13) }, сводка.Crashes[2].Times);
        Assert.Equal(3, сводка.Workers.Today);
        Assert.Equal(new SummaryHighlight(SummaryHighlights.ProShowCrash, 1), сводка.Highlights[0]);
    }

    [Fact]
    public void Всплеск_воркеров_больше_удвоенной_медианы_и_не_меньше_чем_на_пять()
    {
        var история = История(2, 3, 3, 4, 4, 5, 6);
        var падения = Enumerable.Range(0, 9).Select(i => Падение(В(10, i), "fvideo.exe")).ToList();

        var сводка = DailySummaries.Build(Вход(падения, история: история));

        Assert.Equal(new WorkerSpike(9, 4, 7, SpikeState.Yes), сводка.Workers);
        Assert.Contains(new SummaryHighlight(SummaryHighlights.WorkerSpike, 9), сводка.Highlights);
    }

    [Fact]
    public void Удвоенная_медиана_без_запаса_в_пять_не_всплеск()
    {
        var история = История(1, 1, 1, 1, 1, 1, 1);
        var падения = Enumerable.Range(0, 4).Select(i => Падение(В(10, i), "device-encp.exe")).ToList();

        var сводка = DailySummaries.Build(Вход(падения, история: история));

        Assert.Equal(SpikeState.No, сводка.Workers.State);
        Assert.True(сводка.Calm);
    }

    [Fact]
    public void Меньше_семи_прошлых_дней_истории_мало()
    {
        var падения = Enumerable.Range(0, 50).Select(i => Падение(В(10, i), "fvideo.exe")).ToList();

        var сводка = DailySummaries.Build(Вход(падения, история: История(0, 0, 0, 0, 0, 0)));

        Assert.Equal(new WorkerSpike(50, null, 6, SpikeState.InsufficientHistory), сводка.Workers);
    }

    [Fact]
    public void Kernel_Power_41_с_кодом_синего_экрана_и_без()
    {
        var сводка = DailySummaries.Build(Вход(
        [
            Выключение(В(9)),
            Выключение(В(10), синийЭкран: "239", кнопка: "131234567890"),
            Системное(В(11), "Microsoft-Windows-WHEA-Logger", 19),
            Системное(В(12), "EventLog", 6008),
            Системное(В(13), "EventLog", 6005),
            Системное(В(14), "EventLog", 6006),
        ]));

        Assert.Equal(
            new[] { new UnexpectedShutdown(В(9), null, false), new UnexpectedShutdown(В(10), "239", true) },
            сводка.Machine.UnexpectedShutdowns);
        Assert.Equal((1, 0, 1, 1, 1),
            (сводка.Machine.LastAliveReports, сводка.Machine.BugCheckReports, сводка.Machine.Whea, сводка.Machine.Boots, сводка.Machine.CleanShutdowns));
        Assert.Equal(
            new[] { new SummaryHighlight(SummaryHighlights.UnexpectedShutdown, 2), new SummaryHighlight(SummaryHighlights.BugCheck, 1) },
            сводка.Highlights);
    }

    [Fact]
    public void Событие_в_23_59_и_в_00_01_местного_времени_в_разных_днях()
    {
        var вечер = Падение(В(23, 59), "proshow.exe");
        var ночь = Падение(В(0, 1, деньМесяца: 6), "proshow.exe");

        var сводка = DailySummaries.Build(Вход([вечер, ночь]));

        Assert.Equal(new[] { В(23, 59) }, сводка.Crashes.Single().Times);
    }

    [Fact]
    public void День_считается_по_местному_времени_а_не_по_UTC()
    {
        // 22:30 UTC 4 октября — уже 01:30 5 октября по Москве.
        var событие = Падение(new DateTimeOffset(2026, 10, 4, 22, 30, 0, TimeSpan.Zero), "proshow.exe");

        var сводка = DailySummaries.Build(Вход([событие]));

        Assert.Equal(1, сводка.Crashes.Single().Crashes);
    }

    [Fact]
    public void Сеанс_через_полночь_делится_по_часам()
    {
        var сеанс = Сеанс("через-полночь", В(22, 0, деньМесяца: 4), В(2), origin: "watch", причина: null, меток: 1, эпизоды: ["qtime-loop"]);
        var дневной = Сеанс("дневной", В(10), В(13, 30), origin: "wizard");
        var вчерашний = Сеанс("вчерашний", В(10, 0, деньМесяца: 4), В(11, 0, деньМесяца: 4));

        var сводка = DailySummaries.Build(Вход(сеансы: [сеанс, дневной, вчерашний]));

        Assert.Equal(2, сводка.ProShow.Sessions);
        Assert.Equal(5.5, сводка.ProShow.Hours);
        Assert.Equal(new[] { new NamedCount("watch", 1), new NamedCount("wizard", 1) }, сводка.ProShow.ByOrigin);
        Assert.Equal(new[] { new NamedCount(SessionEndReasons.ProgramExited, 1), new NamedCount(DailySummaries.Unfinished, 1) }, сводка.ProShow.ByEnd);
        Assert.Equal(new[] { new NamedCount("qtime-loop", 1) }, сводка.ProShow.Episodes);
    }

    [Fact]
    public void Сеанс_без_программы_не_считается()
    {
        var отказ = Сеанс("отказ", В(10), В(10, 1), причина: SessionEndReasons.NoProgram);

        var сводка = DailySummaries.Build(Вход(сеансы: [отказ]));

        Assert.Equal(0, сводка.ProShow.Sessions);
    }

    [Fact]
    public void Жалобы_дня_со_временем_и_источником()
    {
        var утром = new IncidentRecord(new DateTime(2026, 10, 5, 7, 0, 0, DateTimeKind.Utc), IncidentSources.Wizard, null, null, null, null, "hung", null);
        var вчера = new IncidentRecord(new DateTime(2026, 10, 4, 7, 0, 0, DateTimeKind.Utc), IncidentSources.Cli, null, null, null, null, "none", null);

        var сводка = DailySummaries.Build(Вход(метки: [утром, вчера]));

        Assert.Equal(new[] { new Complaint(В(10), IncidentSources.Wizard, null) }, сводка.ProShow.Complaints);
        Assert.Equal(new[] { new SummaryHighlight(SummaryHighlights.Complaint, 1) }, сводка.Highlights);
    }

    private static EnvironmentEntry Фильтр(string ключ, string merit) =>
        new(EnvironmentSections.DirectShow, 32, ключ, new Dictionary<string, string?> { ["merit"] = merit }, null);

    [Fact]
    public void Разница_слепка_со_вчерашним_по_разделам()
    {
        var вчера = EnvironmentSnapshot.Create(В(9, 0, 4), 0.1, [Фильтр("lav", "0x800000"), Фильтр("ffdshow", "0x200000")]);
        var сегодня = EnvironmentSnapshot.Create(В(9), 0.1, [Фильтр("lav", "0x600000"), Фильтр("madvr", "0x200000")]);

        var сводка = DailySummaries.Build(Вход(слепок: сегодня, прежний: вчера));

        Assert.Equal(new[] { new SectionChanges(EnvironmentSections.DirectShow, 1, 1, 1) }, сводка.Environment.Sections);
        Assert.Equal(new[] { "added madvr", "removed ffdshow", "changed lav" }, сводка.Environment.Entries.Select(e => $"{e.Change} {e.Key}"));
        Assert.Equal(new[] { new SummaryHighlight(SummaryHighlights.EnvironmentChanged, 3) }, сводка.Highlights);
    }

    [Fact]
    public void Тот_же_слепок_изменений_не_даёт()
    {
        var слепок = EnvironmentSnapshot.Create(В(9), 0.1, [Фильтр("lav", "0x800000")]);

        var сводка = DailySummaries.Build(Вход(слепок: слепок, прежний: слепок with { Taken = В(9, 0, 4) }));

        Assert.False(сводка.Environment.Changed);
        Assert.True(сводка.Calm);
    }

    [Fact]
    public void Главное_по_порядку_важности()
    {
        var сводка = DailySummaries.Build(Вход(
            [Выключение(В(8)), Падение(В(9), "proshow.exe")],
            сеансы: [Сеанс("с-отказом", В(10), В(11), отказовEtw: 1)],
            метки: [new IncidentRecord(new DateTime(2026, 10, 5, 9, 0, 0, DateTimeKind.Utc), IncidentSources.Wizard, null, null, null, null, "running", null)],
            место: Место(5),
            перезапуски: [В(16)]));

        Assert.Equal(
            new[]
            {
                SummaryHighlights.ProShowCrash, SummaryHighlights.Complaint, SummaryHighlights.UnexpectedShutdown,
                SummaryHighlights.LowDisk, SummaryHighlights.DoctorTrouble,
            },
            сводка.Highlights.Select(h => h.Kind));
        Assert.Equal(new SummaryHighlight(SummaryHighlights.LowDisk, 1), сводка.Highlights[3]);
        Assert.Equal(new DoctorDay(1, 1, 1000), сводка.Doctor);
    }

    [Fact]
    public void Пустой_день_спокойный_а_пробелы_в_данных_названы()
    {
        var сводка = DailySummaries.Build(Вход() with { Disks = null, EventsCovered = false });

        Assert.True(сводка.Calm);
        Assert.Equal(new[] { SummaryGaps.Events, SummaryGaps.Snapshot, SummaryGaps.Disks }, сводка.Gaps);
    }

    private static Fact Факт(long номер, double секунд, string вид, string данные) =>
        new(номер, TimeSpan.FromSeconds(секунд), "s", null, вид, JsonDocument.Parse(данные).RootElement.Clone());

    [Fact]
    public void Краткая_запись_сеанса_из_фактов_журнала()
    {
        var факты = new[]
        {
            Факт(1, 0, ProgramFactKinds.SessionStarted, """{"startedAt":"2026-10-05T10:00:00+03:00","origin":"watch"}"""),
            Факт(2, 5, ProgramFactKinds.EtwState, """{"state":"ready","lostEvents":0}"""),
            Факт(3, 60, ProgramFactKinds.EtwState, """{"state":"failed","error":"helper-lost"}"""),
            Факт(4, 90, ProgramFactKinds.Episode, """{"pattern":"qtime-loop","image":"qtime.exe"}"""),
            Факт(5, 120, ProgramFactKinds.Incident, """{"source":"wizard","note":null}"""),
            Факт(6, 3600, ProgramFactKinds.SessionFinished, """{"reason":"program-exited"}"""),
        };

        var запись = SessionBrief.FromFacts("20261005-070000-000", факты);

        Assert.NotNull(запись);
        Assert.Equal((В(10), В(11), "watch", SessionEndReasons.ProgramExited, 1, 1),
            (запись.Started, запись.Ended, запись.Origin, запись.EndReason, запись.Incidents, запись.EtwFailures));
        Assert.Equal(new[] { "qtime-loop" }, запись.Episodes);
    }

    [Fact]
    public void Оборванный_сеанс_кончается_последним_фактом_без_причины()
    {
        var факты = new[]
        {
            Факт(1, 0, ProgramFactKinds.SessionStarted, """{"startedAt":"2026-10-05T10:00:00+03:00","origin":null}"""),
            Факт(2, 1800, ProgramFactKinds.FileIo, "{}"),
        };

        var запись = SessionBrief.FromFacts("s", факты);

        Assert.Equal((В(10, 30), (string?)null, (string?)null), (запись!.Ended, запись.EndReason, запись.Origin));
    }

    [Fact]
    public void Журнал_без_начала_сеанса_краткой_записи_не_даёт()
    {
        Assert.Null(SessionBrief.FromFacts("s", [Факт(1, 0, ProgramFactKinds.FileIo, "{}")]));
    }

    [Fact]
    public void Сводка_и_краткая_запись_проходят_JSON_туда_и_обратно()
    {
        var вчера = EnvironmentSnapshot.Create(В(9, 0, 4), 0.1, [Фильтр("lav", "0x800000")]);
        var сегодня = EnvironmentSnapshot.Create(В(9), 0.1, [Фильтр("lav", "0x600000")]);
        var сводка = DailySummaries.Build(Вход(
            [Падение(В(9), "proshow.exe"), Выключение(В(10), "239")],
            сеансы: [Сеанс("a", В(10), В(11), эпизоды: ["qtime-loop"])],
            история: История(1, 1, 1, 1, 1, 1, 1),
            слепок: сегодня, прежний: вчера));
        var запись = Сеанс("a", В(10), В(11), эпизоды: ["qtime-loop"]);

        var текст = DailySummaryJson.Serialize(сводка);
        var обратно = DailySummaryJson.Deserialize(текст);

        Assert.Equal(текст, DailySummaryJson.Serialize(обратно));
        Assert.Equal(DailySummaryJson.Serialize(запись), DailySummaryJson.Serialize(DailySummaryJson.DeserializeBrief(DailySummaryJson.Serialize(запись))));
        Assert.Contains("\"day\": \"2026-10-05\"", текст);
    }

    [Theory]
    [InlineData("proshow.exe", "proshow")]
    [InlineData(@"C:\Program Files (x86)\Photodex\ProShow Producer\ProShow.EXE", "proshow")]
    [InlineData("AfterFX.exe", "afterfx")]
    [InlineData("device-encp", "device-encp")]
    public void Имя_образа_без_каталога_и_расширения(string параметр, string имя)
    {
        Assert.Equal(имя, DailySummaries.ImageName(параметр));
    }
}
