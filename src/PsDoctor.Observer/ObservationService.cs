using System.Text;
using System.Text.Json;
using PsDoctor.Core.Observation;
using PsDoctor.Core.Scenarios;
using PsDoctor.Infrastructure.Observation;

namespace PsDoctor.Observer;

/// <summary>
/// Сеансы и сценарии наблюдателя. Живой сеанс — не больше одного, выполняемый сценарий — не больше одного:
/// программа на стенде одна, и второй экземпляр путает кэши и число процессов.
/// </summary>
public sealed class ObservationService : IAsyncDisposable
{
    private readonly string directory;
    private readonly IProgramLauncher launcher;
    private readonly ObserverHealth build;
    private readonly TimeSpan? actionTimeout;
    private readonly Func<DateTime> utcNow;
    private readonly Lock gate = new();
    private ObservationSession? current;
    private CancellationTokenSource? scenario;
    // Сеансы, открытые под запуск или подключение, которые отвергнуты до начала наблюдения. В списке их нет,
    // журнал удаляется, как только сеанс закрыт: пустой сеанс нельзя выдавать за наблюдение.
    private readonly HashSet<string> discarded = [];
    private readonly RetentionLimits? retention;
    // Пометка закрытого сеанса не меняется: журнал читается ради неё один раз за жизнь наблюдателя.
    private readonly Dictionary<string, bool> marks = [];
    private readonly Lock sweeping = new();
    private readonly Lock incidents = new();

    /// <summary>Файл меток инцидентов в каталоге сеансов: только дописывается, хранение его не удаляет.</summary>
    public const string IncidentsFile = "incidents.jsonl";

    /// <summary>Сколько после конца сеанса метка ещё относится к нему: падение или перезагрузка, вход, нажатие.</summary>
    public static readonly TimeSpan RecentWindow = TimeSpan.FromMinutes(30);

    /// <summary>Сколько последних сеансов смотреть в поисках <see cref="IncidentRecord.Recent"/>.</summary>
    private const int RecentCandidates = 3;

    /// <param name="retention">Пределы хранения; <c>null</c> — ничего не удаляется, как на стенде без установщика.</param>
    public ObservationService(string directory, IProgramLauncher launcher, ObserverHealth build, TimeSpan? actionTimeout = null,
        Func<DateTime>? utcNow = null, RetentionLimits? retention = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        ArgumentNullException.ThrowIfNull(launcher);
        ArgumentNullException.ThrowIfNull(build);
        this.directory = directory;
        this.launcher = launcher;
        this.build = build;
        this.actionTimeout = actionTimeout;
        this.utcNow = utcNow ?? (() => DateTime.UtcNow);
        this.retention = retention;
    }

    public string DataDirectory => directory;

    /// <summary>Итог приёма сценария: принят — сеанс и номер, после которого пойдут его факты; иначе отказ.</summary>
    public sealed record RunResult(RunScenarioAccepted? Accepted, int Status, ObserverError? Error);

    /// <param name="origin">Кто начал сеанс, <see cref="SessionOrigins"/>; действует, только если сценарий открывает сеанс.</param>
    public RunResult Run(string text, string? origin = null)
    {
        var parsed = ScenarioParser.Parse(text ?? "");
        if (parsed.Scenario is null)
        {
            return Refuse(StatusCodes.Status400BadRequest, new ObserverError(ObserverErrors.BadScenario, parsed.Errors));
        }

        lock (gate)
        {
            if (scenario is not null)
            {
                return Refuse(StatusCodes.Status409Conflict, new ObserverError(ObserverErrors.ScenarioRunning));
            }

            var session = current is { IsFinishing: false } ? current : null;
            if (parsed.Scenario.Steps[0] is LaunchStep)
            {
                // Отказ до сеанса, с кодом: запущенную программу не трогаем и сеанс под неё не открываем.
                // Сеанс, который закрывается, но ещё не дописал последний факт, — тоже живая программа.
                if (current is { Log.IsCompleted: false } || launcher.IsProgramRunning())
                {
                    return Refuse(StatusCodes.Status409Conflict, new ObserverError(ObserverErrors.ProgramRunning));
                }
                session = ObservationSession.Open(
                    directory,
                    SessionIds.New(utcNow(), id => File.Exists(Path.Combine(directory, id + SessionIds.JournalExtension))),
                    build,
                    OnFinished,
                    showPath: ((LaunchStep)parsed.Scenario.Steps[0]).ShowPath,
                    origin: origin);
                current = session;
                if (OperatingSystem.IsWindows() && launcher is ProShowLauncher proshow)
                {
                    // Без помощника ETW сеанс идёт с фактом etw-state unavailable, а не отказом (Э6.2, часть А).
                    var opened = session;
                    session.SetTrace(EtwBridge.Open(directory, session.Id, () => opened.ProcessId ?? 0,
                        Path.GetFileName(proshow.ProgramPath), session.Log));
                }
            }
            else if (session is null)
            {
                return Refuse(StatusCodes.Status409Conflict, new ObserverError(ObserverErrors.NoSession));
            }

            // Пассивному сеансу — только шаги оператора и ожидания; отказ целиком, до первого шага.
            if (session.IsPassive && parsed.Scenario.DrivesProgram)
                return Refuse(StatusCodes.Status409Conflict, new ObserverError(ObserverErrors.PassiveSession));

            var after = session.Log.LastNumber;
            var cancellation = new CancellationTokenSource();
            scenario = cancellation;
            var task = session.RunScenario(parsed.Scenario, new SessionActions(session, launcher), actionTimeout, cancellation.Token);
            _ = task.ContinueWith(_ => OnScenarioDone(cancellation), TaskScheduler.Default);
            return new RunResult(new RunScenarioAccepted(session.Id, after), StatusCodes.Status202Accepted, null);
        }
    }

    /// <summary>Начинает сеанс чтения уже работающего ProShow; запуск и команды ему запрещены.</summary>
    public (AttachAccepted? Accepted, int Status, ObserverError? Error) Attach(string? origin = null)
    {
        lock (gate)
        {
            if (current is { Log.IsCompleted: false } || scenario is not null)
                return (null, StatusCodes.Status409Conflict, new ObserverError(ObserverErrors.ProgramRunning));
            if (launcher is not IProgramAttacher attacher)
                return (null, StatusCodes.Status503ServiceUnavailable, new ObserverError(ObserverErrors.AttachFailed));

            ProgramTarget target;
            try { target = attacher.FindRunning(); }
            catch (ProgramAttachException error)
            {
                return (null, StatusCodes.Status409Conflict, new ObserverError(error.Reason));
            }

            var session = ObservationSession.Open(directory,
                SessionIds.New(utcNow(), id => File.Exists(Path.Combine(directory, id + SessionIds.JournalExtension))),
                build, OnFinished, origin: origin);
            current = session;
            try
            {
                var run = session.Attach(target, attacher);
                if (OperatingSystem.IsWindows() && launcher is ProShowLauncher proshow)
                {
                    Func<IReadOnlyCollection<int>?>? processes = run is AttachedRun attached ? attached.LiveProcessIds : null;
                    session.SetTrace(EtwBridge.Open(directory, session.Id, () => target.ProcessId,
                        Path.GetFileName(proshow.ProgramPath), session.Log, processes));
                }
                return (new AttachAccepted(session.Id, target.ProcessId, target.StartedUtc),
                    StatusCodes.Status201Created, null);
            }
            catch (ProgramAttachException error)
            {
                Discard(session);
                return (null, StatusCodes.Status409Conflict, new ObserverError(error.Reason));
            }
        }
    }
    /// <summary>Что наблюдатель делает сейчас — для <c>/health</c>. Дёшево: без журнала, один перечень процессов.</summary>
    /// <remarks>
    /// Без замка <c>gate</c> нарочно: под ним запускается ETW и идёт подключение, это секунды, а <c>/health</c> спрашивает
    /// сторож — ждущий ответ он счёл бы зависанием. Ссылки читаются целиком; состояние на миг старше — не беда для лампы.
    /// </remarks>
    public ObserverActivity Activity()
    {
        var session = Volatile.Read(ref current);
        var live = session is { Log.IsCompleted: false } ? session : null;
        var running = Volatile.Read(ref scenario) is not null;
        if (live is not null)
        {
            return new ObserverActivity(live.IsPassive ? ProgramStates.Attached : ProgramStates.Launched,
                live.ProcessId, live.Id, running);
        }
        // Та же проверка, что отказывает launch с program-running: лампа и отказ не разойдутся.
        return new ObserverActivity(launcher.IsProgramRunning() ? ProgramStates.Unobserved : ProgramStates.None,
            null, null, running);
    }

    /// <summary>
    /// Метка инцидента: факт <c>incident</c> в живой сеанс, который не закрывается, и строка в файл меток — всегда.
    /// <c>null</c> — метка не легла никуда.
    /// </summary>
    /// <remarks>
    /// Без замка <c>gate</c>, как <see cref="Activity"/>: подключение держит его до десяти секунд, пока стартует ETW,
    /// а метку монтажёр ставит в тот миг, когда ему плохо. Сеанс, открытый таким подключением, метку получит;
    /// если подключение затем отвергнуто, в файле останется имя без журнала.
    /// </remarks>
    public IncidentRecord? Incident(string source, string? note)
    {
        ArgumentException.ThrowIfNullOrEmpty(source);
        var at = utcNow();
        var session = Volatile.Read(ref current);
        var fact = session is { Log.IsCompleted: false }
            ? session.TryRecord(ProgramFactKinds.Incident, new { source, note, atUtc = at })
            : null;
        var activity = Activity();
        var marked = fact is null ? null : session!.Id;
        var record = new IncidentRecord(at, source, note, marked, fact?.Number, Recent(at, marked),
            activity.Program, activity.ProcessId);
        return AppendIncident(record) || fact is not null ? record : null;
    }

    /// <summary>Все метки по порядку. Битая строка, в том числе недописанный хвост после обрыва, пропускается.</summary>
    public IReadOnlyList<IncidentRecord> Incidents()
    {
        string[] lines;
        try
        {
            lock (incidents)
            {
                var path = Path.Combine(directory, IncidentsFile);
                lines = File.Exists(path) ? File.ReadAllLines(path, Encoding.UTF8) : [];
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
        var result = new List<IncidentRecord>();
        foreach (var line in lines)
        {
            try
            {
                if (JsonSerializer.Deserialize<IncidentRecord>(line, ObservationJson.Options) is { Source: not null, Program: not null } record)
                {
                    result.Add(record);
                }
            }
            catch (JsonException)
            {
            }
        }
        return result;
    }

    private bool AppendIncident(IncidentRecord record)
    {
        var line = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(record, ObservationJson.Options) + "\n");
        try
        {
            lock (incidents)
            {
                Directory.CreateDirectory(directory);
                using var stream = new FileStream(Path.Combine(directory, IncidentsFile), FileMode.OpenOrCreate,
                    FileAccess.ReadWrite, FileShare.Read);
                if (stream.Length > 0)
                {
                    // Прошлая запись оборвана посередине строки: новая метка начинается с новой строки, а не склеивается с ней.
                    stream.Seek(-1, SeekOrigin.End);
                    if (stream.ReadByte() != '\n')
                    {
                        stream.WriteByte((byte)'\n');
                    }
                }
                stream.Seek(0, SeekOrigin.End);
                stream.Write(line);
                stream.Flush(flushToDisk: true);
            }
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Самый поздно кончившийся из последних сеансов, кроме <paramref name="except"/>, если он кончился не раньше
    /// <see cref="RecentWindow"/> до метки. Конец — время из имени плюс время последнего целого факта, поэтому подходит
    /// и оборванный сеанс. Отвергнутый подключением или запуском сеанс кончается <c>no-program</c> и не считается.
    /// </summary>
    private string? Recent(DateTime at, string? except)
    {
        IEnumerable<string> ids;
        try
        {
            ids = Directory.Exists(directory)
                ? Directory.EnumerateFiles(directory, "*" + SessionIds.JournalExtension)
                    .Select(Path.GetFileNameWithoutExtension)
                    .Where(SessionIds.IsValid)
                    .Select(id => id!)
                    .Where(id => id != except)
                    .OrderDescending(StringComparer.Ordinal)
                    .Take(RecentCandidates)
                    .ToList()
                : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        string? recent = null;
        var latest = at - RecentWindow;
        foreach (var id in ids)
        {
            if (SessionIds.StartedUtc(id) is not { } started || LastFact(id) is not { } last)
            {
                continue;
            }
            if (last.Kind == ProgramFactKinds.SessionFinished
                && last.Data.TryGetProperty("reason", out var reason)
                && reason.ValueKind == JsonValueKind.String
                && reason.GetString() == SessionEndReasons.NoProgram)
            {
                continue;
            }
            var end = started + last.Elapsed;
            if (end >= latest)
            {
                recent = id;
                latest = end;
            }
        }
        return recent;
    }

    /// <summary>Отменяет выполняемый сценарий. Сеанс — <c>null</c>, если отменять нечего.</summary>
    public string? Cancel()
    {
        lock (gate)
        {
            if (scenario is null || current is null)
            {
                return null;
            }
            scenario.Cancel();
            return current.Id;
        }
    }

    /// <summary>Передаёт подтверждение оператора выполняемому сценарию. Сеанс — <c>null</c>, если сценария нет.</summary>
    public string? Confirm()
    {
        lock (gate)
        {
            if (scenario is null || current is null)
            {
                return null;
            }
            current.Confirm();
            return current.Id;
        }
    }

    /// <summary>Прекращает наблюдение: сценарий отменяется, программа продолжает работать.</summary>
    public async Task<(int Status, ObserverError? Error)> StopAsync(string id)
    {
        ObservationSession? session;
        lock (gate)
        {
            session = current?.Id == id ? current : null;
            if (session is not null)
            {
                scenario?.Cancel();
            }
        }
        if (session is null)
        {
            return JournalPath(id) is null
                ? (StatusCodes.Status404NotFound, new ObserverError(ObserverErrors.UnknownSession))
                : (StatusCodes.Status409Conflict, new ObserverError(ObserverErrors.SessionFinished));
        }
        await session.FinishAsync(SessionEndReasons.Stopped).ConfigureAwait(false);
        return (StatusCodes.Status200OK, null);
    }

    public IReadOnlyList<SessionSummary> Sessions()
    {
        ObservationSession? live;
        lock (gate)
        {
            live = current;
        }
        HashSet<string> hidden;
        lock (gate)
        {
            hidden = [.. discarded];
        }
        var ids = Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "*" + SessionIds.JournalExtension)
                .Select(Path.GetFileNameWithoutExtension)
                .Where(SessionIds.IsValid)
                .Select(id => id!)
                .Where(id => !hidden.Contains(id))
            : [];
        return ids
            .Order(StringComparer.Ordinal)
            .Select(id => id == live?.Id
                ? new SessionSummary(id, !live.Log.IsCompleted, live.Log.LastNumber, EndsFinished(id))
                : new SessionSummary(id, false, null, EndsFinished(id)))
            .ToList();
    }

    /// <summary>Сколько байтов хвоста журнала читать ради последней строки: строка факта — сотни байтов.</summary>
    private const int JournalTail = 16 * 1024;

    /// <summary>
    /// Журнал кончается фактом <c>session-finished</c>. Весь журнал ради этого не читается: хватает хвоста файла.
    /// Нечитаемый файл и недописанная последняя строка — оборванный сеанс, а не доведённый до конца.
    /// </summary>
    private bool EndsFinished(string id)
    {
        var last = TailLines(id).LastOrDefault();
        if (last is null)
        {
            return false;
        }
        try
        {
            using var fact = JsonDocument.Parse(last);
            return fact.RootElement.TryGetProperty("kind", out var kind)
                && kind.ValueKind == JsonValueKind.String
                && kind.GetString() == ProgramFactKinds.SessionFinished;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Последний целый факт журнала по хвосту файла: недописанная последняя строка пропускается.</summary>
    private Fact? LastFact(string id)
    {
        var lines = TailLines(id);
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            try
            {
                if (JsonSerializer.Deserialize<Fact>(lines[i], ObservationJson.Options) is { Kind: not null, Data.ValueKind: JsonValueKind.Object } fact)
                {
                    return fact;
                }
            }
            catch (JsonException)
            {
            }
        }
        return null;
    }

    /// <summary>Строки хвоста журнала; первая может быть обрезана. Нечитаемый файл — пусто.</summary>
    private string[] TailLines(string id)
    {
        try
        {
            var path = Path.Combine(directory, id + SessionIds.JournalExtension);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var size = (int)Math.Min(stream.Length, JournalTail);
            if (size == 0)
            {
                return [];
            }
            stream.Seek(-size, SeekOrigin.End);
            var tail = new byte[size];
            stream.ReadExactly(tail, 0, size);
            return Encoding.UTF8.GetString(tail)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Открытые диалоги живого сеанса; отказ — у закрытого и неизвестного.</summary>
    public (IReadOnlyList<DialogInfo>? Dialogs, int Status, ObserverError? Error) Dialogs(string id)
    {
        ObservationSession? session;
        lock (gate)
        {
            session = current?.Id == id ? current : null;
        }
        if (session is not null && !session.IsFinishing)
        {
            return (session.Dialogs(), StatusCodes.Status200OK, null);
        }
        return session is null && JournalPath(id) is null
            ? (null, StatusCodes.Status404NotFound, new ObserverError(ObserverErrors.UnknownSession))
            : (null, StatusCodes.Status409Conflict, new ObserverError(ObserverErrors.SessionFinished));
    }

    /// <summary>Журнал в памяти, если сеанс ещё открыт в этом процессе.</summary>
    public FactLog? Live(string id)
    {
        lock (gate)
        {
            return current?.Id == id ? current.Log : null;
        }
    }

    /// <summary>Путь к журналу сеанса на диске или <c>null</c>, если такого сеанса нет.</summary>
    public string? JournalPath(string id)
    {
        if (!SessionIds.IsValid(id))
        {
            return null;
        }
        lock (gate)
        {
            if (discarded.Contains(id))
            {
                return null;
            }
        }
        var path = Path.Combine(directory, id + SessionIds.JournalExtension);
        return File.Exists(path) ? path : null;
    }

    public IReadOnlyList<SessionArtifact>? Artifacts(string id)
    {
        var facts = ReadFacts(id);
        if (facts is null) return null;
        return facts
            .Where(f => f.Kind == ProgramFactKinds.RenderArtifacts)
            .SelectMany(f => JsonSerializer.Deserialize<RenderArtifacts>(f.Data.GetRawText(), ObservationJson.Options)?.Items ?? [])
            .ToList();
    }

    public (SessionArtifact Artifact, string Path)? ArtifactFile(string id, string artifactId)
    {
        var facts = ReadFacts(id);
        if (facts is null) return null;
        var launched = facts.FirstOrDefault(f => f.Kind == ProgramFactKinds.ProgramLaunched);
        var root = launched is { } started
            && started.Data.TryGetProperty("workingDirectory", out var workingDirectory)
            && workingDirectory.ValueKind == JsonValueKind.String
            ? workingDirectory.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(root)) return null;
        var rootPath = root!;
        var artifact = facts
            .Where(f => f.Kind == ProgramFactKinds.RenderArtifacts)
            .SelectMany(f => JsonSerializer.Deserialize<RenderArtifacts>(f.Data.GetRawText(), ObservationJson.Options)?.Items ?? [])
            .FirstOrDefault(item => item.Id == artifactId);
        if (artifact is null) return null;
        var path = Path.GetFullPath(Path.Combine(rootPath, artifact.Name));
        var directory = Path.GetFullPath(rootPath);
        if (!path.StartsWith(directory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || !File.Exists(path)) return null;
        var info = new FileInfo(path);
        if (info.Length != artifact.Bytes || info.LastWriteTimeUtc != artifact.LastWriteUtc) return null;
        return (artifact, path);
    }

    private IReadOnlyList<Fact>? ReadFacts(string id)
    {
        var live = Live(id);
        if (live is not null) return live.After(0);
        var path = JournalPath(id);
        if (path is null) return null;
        try
        {
            using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 64 * 1024);
            return FactJournalReader.ReadAfter(reader, 0).ToList();
        }
        catch (IOException) { return null; }
        catch (InvalidDataException) { return null; }
    }

    /// <summary>Остановка наблюдателя: сеанс закрывается, программа продолжает работать.</summary>
    public async ValueTask DisposeAsync()
    {
        ObservationSession? session;
        lock (gate)
        {
            session = current;
            scenario?.Cancel();
        }
        if (session is not null)
        {
            await session.FinishAsync(SessionEndReasons.ObserverShutdown).ConfigureAwait(false);
        }
    }

    private static RunResult Refuse(int status, ObserverError error) => new(null, status, error);

    private void OnScenarioDone(CancellationTokenSource cancellation)
    {
        lock (gate)
        {
            if (scenario == cancellation)
            {
                scenario = null;
            }
        }
        cancellation.Dispose();
    }

    /// <summary>
    /// Сеанс отвергнут до начала наблюдения: из списка он пропадает сразу, журнал удаляется после закрытия.
    /// Зовётся под <see cref="gate"/>, поэтому закрытие уходит в фон: оно само берёт замок в <see cref="OnFinished"/>.
    /// </summary>
    private void Discard(ObservationSession session)
    {
        discarded.Add(session.Id);
        _ = Task.Run(async () =>
        {
            await session.FinishAsync(SessionEndReasons.NoProgram).ConfigureAwait(false);
            try
            {
                File.Delete(session.JournalPath);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Файл остаётся на диске, но в список и в API не попадает до перезапуска наблюдателя.
            }
        });
    }

    private void OnFinished(ObservationSession session)
    {
        lock (gate)
        {
            if (current == session)
            {
                current = null;
            }
        }
        if (retention is not null)
        {
            _ = Task.Run(Sweep);
        }
    }

    /// <summary>
    /// Удаляет сеансы сверх пределов хранения: журнал и файлы ETW. Живой и отвергнутый сеансы не трогает.
    /// Зовётся при старте наблюдателя и после каждого закрытого сеанса; два прохода сразу не идут.
    /// Файл, который не удалился, останется до следующего прохода.
    /// </summary>
    public void Sweep()
    {
        if (retention is null || !Directory.Exists(directory))
        {
            return;
        }
        lock (sweeping)
        {
            HashSet<string> skip;
            lock (gate)
            {
                skip = [.. discarded];
                if (current is not null)
                {
                    skip.Add(current.Id);
                }
            }

            var etwDirectory = EtwFiles.Directory(directory);
            var etw = Directory.Exists(etwDirectory)
                ? Directory.EnumerateFiles(etwDirectory)
                    .Select(path => (Path: path, Id: Path.GetFileName(path).Split('.')[0]))
                    .Where(file => SessionIds.IsValid(file.Id))
                    .ToLookup(file => file.Id, file => file.Path)
                : Enumerable.Empty<string>().ToLookup(_ => "", _ => "");
            // Метка после конца сеанса в журнал не ложится: такой сеанс помечен строкой файла меток.
            var named = Incidents()
                .SelectMany(incident => new[] { incident.Session, incident.Recent })
                .OfType<string>()
                .ToHashSet(StringComparer.Ordinal);

            var stored = new List<StoredSession>();
            foreach (var journal in Directory.EnumerateFiles(directory, "*" + SessionIds.JournalExtension))
            {
                var id = Path.GetFileNameWithoutExtension(journal);
                if (!SessionIds.IsValid(id) || skip.Contains(id) || SessionIds.StartedUtc(id) is not { } started)
                {
                    continue;
                }
                var bytes = Size(journal) + etw[id].Sum(Size);
                stored.Add(new StoredSession(id, started, bytes, named.Contains(id) || IsMarked(id, journal)));
            }

            foreach (var id in Retention.Expired(stored, retention, utcNow()))
            {
                foreach (var path in etw[id].Append(Path.Combine(directory, id + SessionIds.JournalExtension)))
                {
                    try
                    {
                        File.Delete(path);
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                    }
                }
                marks.Remove(id);
            }
        }
    }

    private bool IsMarked(string id, string journal)
    {
        if (marks.TryGetValue(id, out var marked))
        {
            return marked;
        }
        try
        {
            using var reader = new StreamReader(new FileStream(journal, FileMode.Open, FileAccess.Read, FileShare.ReadWrite), Encoding.UTF8);
            marked = Retention.IsMarked(FactJournalReader.ReadAfter(reader, 0));
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            // Испорченный журнал не помечен: он уйдёт по обычному сроку, а не будет жить полгода.
            marked = false;
        }
        marks[id] = marked;
        return marked;
    }

    private static long Size(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }
}
