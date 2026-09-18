using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using PsDoctor.Cli;
using PsDoctor.Core.Observation;
using PsDoctor.Core.Scenarios;
using PsDoctor.Observer;
using Xunit;

namespace PsDoctor.Cli.Tests;

/// <summary>
/// <c>psdoctor observe</c> против настоящего наблюдателя на петле с подменённым запуском — тот же путь,
/// каким агент на Linux-хосте ходит на стенд, только без Windows.
/// </summary>
public sealed class ObserveCommandTests : IAsyncLifetime
{
    private const string Ключ = "cli-test-key-0123456789";

    private readonly string _каталог = Directory.CreateTempSubdirectory("psdoctor-observe-").FullName;
    private readonly Запуск _запуск = new();
    private WebApplication _наблюдатель = null!;
    private string _файлКлюча = null!;

    public async Task InitializeAsync()
    {
        _файлКлюча = Path.Combine(_каталог, "observer.key");
        await File.WriteAllTextAsync(_файлКлюча, Ключ + "\n");
        _наблюдатель = ObserverHost.Build(new ObserverOptions(IPAddress.Loopback, 0, Ключ, Path.Combine(_каталог, "sessions")), _запуск);
        await _наблюдатель.StartAsync();
    }

    public async Task DisposeAsync()
    {
        // Остановка, а не только освобождение: она закрывает живой сеанс и его журнал, иначе Windows не отдаст каталог.
        await _наблюдатель.StopAsync();
        await _наблюдатель.DisposeAsync();
        Directory.Delete(_каталог, recursive: true);
    }

    private Task<(int Code, string[] Out, string Err)> Observe(params string[] args) => Observe(null, args);

    private async Task<(int Code, string[] Out, string Err)> Observe(HttpMessageHandler? handler, params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        using var время = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var code = await ObserveCommand.RunAsync(
            ["--url", _наблюдатель.Urls.Single(), "--key-file", _файлКлюча, .. args],
            TextReader.Null,
            stdout,
            stderr,
            _ => null,
            время.Token,
            handler);
        return (code, stdout.ToString().Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries), stderr.ToString());
    }

    [Fact]
    public async Task Run_выводит_факты_сценария_и_даёт_ноль()
    {
        var (code, stdout, stderr) = await Observe("run", "--step", "launch \"C:\\lab\\p\\1.psh\"");

        Assert.Equal(ObserveExitCodes.Done, code);
        Assert.Contains("сеанс ", stderr, StringComparison.Ordinal);
        var виды = stdout.Select(line => JsonSerializer.Deserialize<Fact>(line, ObservationJson.Options)!.Kind).ToList();
        Assert.Equal(ScenarioFactKinds.ScenarioStarted, виды[0]);
        Assert.Contains(ProgramFactKinds.ProgramLaunched, виды);
        Assert.Equal(ScenarioFactKinds.ScenarioFinished, виды[^1]);
    }

    [Fact]
    public async Task Сорвавшийся_сценарий_даёт_единицу()
    {
        var (code, stdout, _) = await Observe("run", "--step", "launch \"C:\\lab\\p\\1.psh\"", "--step", "close");

        Assert.Equal(ObserveExitCodes.ScenarioNotCompleted, code);
        Assert.Contains("\"failed\"", stdout[^1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dialogs_без_сеанса_отказ_при_живом_строка_на_диалог()
    {
        var (безСеанса, _, _) = await Observe("dialogs");
        await Observe("run", "--step", "launch \"C:\\lab\\p\\1.psh\"");

        var (code, stdout, _) = await Observe("dialogs");

        Assert.Equal(ObserveExitCodes.Refused, безСеанса);
        Assert.Equal(ObserveExitCodes.Done, code);
        var диалог = JsonSerializer.Deserialize<DialogInfo>(Assert.Single(stdout), ObservationJson.Options)!;
        Assert.Equal(Запуск.Диалог.Buttons, диалог.Buttons);
    }

    [Fact]
    public async Task Отказ_наблюдателя_даёт_двойку_и_тело_в_stdout()
    {
        var (code, stdout, _) = await Observe("run", "--step", "прыжок");

        Assert.Equal(ObserveExitCodes.Refused, code);
        var отказ = JsonSerializer.Deserialize<ObserverError>(Assert.Single(stdout), ObservationJson.Options)!;
        Assert.Equal(ObserverErrors.BadScenario, отказ.Error);
        Assert.Equal(1, Assert.Single(отказ.Errors!).Line);
    }

    [Fact]
    public async Task Чужой_ключ_сбой_окружения()
    {
        await File.WriteAllTextAsync(_файлКлюча, "wrong-key");

        var (code, stdout, stderr) = await Observe("health");

        Assert.Equal(ObserveExitCodes.Environment, code);
        Assert.Empty(stdout);
        Assert.Contains("401", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Без_адреса_сбой_окружения()
    {
        var stderr = new StringWriter();

        var code = await ObserveCommand.RunAsync(["health"], TextReader.Null, new StringWriter(), stderr, _ => null);

        Assert.Equal(ObserveExitCodes.Environment, code);
        Assert.Contains(ObserveCommand.UrlVariable, stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Facts_с_номера_после_перезапуска_клиента_добирают_журнал_до_строки()
    {
        var (_, первые, stderr) = await Observe("run", "--step", "launch \"C:\\lab\\p\\1.psh\"");
        var сеанс = stderr.Split(' ', ',')[1];
        var последний = JsonSerializer.Deserialize<Fact>(первые[^1], ObservationJson.Options)!.Number;
        var хвост = Observe("facts", сеанс, "--after", последний.ToString(), "--follow");
        _запуск.Выйти();

        var (code, остальные, _) = await хвост;

        Assert.Equal(ObserveExitCodes.Done, code);
        var журнал = await File.ReadAllLinesAsync(Path.Combine(_каталог, "sessions", сеанс + ".jsonl"));
        // Факт номер 1 — открытие сеанса, он раньше сценария: run его не выводит.
        Assert.Equal(журнал.Length, 1 + первые.Length + остальные.Length);
        Assert.Equal(журнал[^1], остальные[^1]);
    }

    [Fact]
    public async Task Оборванный_поток_досматривается_после_переподключения()
    {
        using var обрыв = new Обрыв(событий: 1);

        var прогон = Observe(обрыв, "run", "--follow", "--step", "launch \"C:\\lab\\p\\1.psh\"");
        await _запуск.Запущен;
        _запуск.Выйти();
        var (code, stdout, stderr) = await прогон;

        Assert.Equal(1, обрыв.Обрывов);
        Assert.Equal(ObserveExitCodes.Done, code);
        Assert.Contains("оборван", stderr, StringComparison.Ordinal);
        var сеанс = stderr.Split(' ', ',')[1];
        var журнал = await File.ReadAllLinesAsync(Path.Combine(_каталог, "sessions", сеанс + ".jsonl"));
        // Факт номер 1 — открытие сеанса, он раньше сценария: run его не выводит. Остальное досмотрено до конца.
        Assert.Equal(журнал.Length, 1 + stdout.Length);
        Assert.Equal(журнал[^1], stdout[^1]);
        var номера = stdout.Select(line => JsonSerializer.Deserialize<Fact>(line, ObservationJson.Options)!.Number).ToList();
        Assert.Equal(номера.Distinct(), номера);
    }

    /// <summary>Рвёт первый поток фактов после нескольких событий: соединение кончилось, сеанс — нет.</summary>
    private sealed class Обрыв(int событий) : DelegatingHandler(new SocketsHttpHandler())
    {
        public int Обрывов { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            var ответ = await base.SendAsync(request, cancellationToken);
            if (Обрывов == 0 && request.RequestUri!.AbsolutePath.EndsWith("/stream", StringComparison.Ordinal))
            {
                Обрывов++;
                var поток = await ответ.Content.ReadAsStreamAsync(cancellationToken);
                ответ.Content = new StreamContent(new Обрезок(поток, событий));
            }
            return ответ;
        }
    }

    /// <summary>Поток, кончающийся посреди сеанса: байт за байтом до нужного числа пустых строк SSE, дальше конец файла.</summary>
    private sealed class Обрезок(Stream inner, int событий) : Stream
    {
        private bool _перенос;
        private int _счёт;
        private bool _кончился;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            if (_кончился || count == 0)
            {
                return 0;
            }
            var прочитано = inner.Read(buffer, offset, 1);
            return прочитано == 0 ? 0 : Счесть(buffer[offset]);
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_кончился || buffer.Length == 0)
            {
                return 0;
            }
            var прочитано = await inner.ReadAsync(buffer[..1], cancellationToken);
            return прочитано == 0 ? 0 : Счесть(buffer.Span[0]);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }
            base.Dispose(disposing);
        }

        /// <summary>Пустая строка кончает событие SSE; набралось нужное число — дальше поток молчит.</summary>
        private int Счесть(byte знак)
        {
            if (знак == (byte)'\n')
            {
                if (_перенос && ++_счёт >= событий)
                {
                    _кончился = true;
                }
                _перенос = true;
            }
            else if (знак != (byte)'\r')
            {
                _перенос = false;
            }
            return 1;
        }
    }

    /// <summary>Подменённый запуск: программа живёт, пока тест не скажет выйти.</summary>
    private sealed class Запуск : IProgramLauncher, IProgramRun
    {
        private readonly TaskCompletionSource _запущен = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private IFactRecorder? _факты;
        private IProgramEvents? _события;

        /// <summary>Выполняется, когда сценарий дошёл до запуска программы.</summary>
        public Task Запущен => _запущен.Task;

        public int ProcessId => 1000;

        public bool IsProgramRunning() => false;

        public IProgramRun Launch(string showPath, IFactRecorder facts, IProgramEvents events)
        {
            _факты = facts;
            _события = events;
            facts.Record(ProgramFactKinds.ProgramLaunched, new ProgramLaunched("proshow.exe", showPath, null, true), ProcessId);
            _запущен.TrySetResult();
            return this;
        }

        public void Выйти()
        {
            _факты!.Record(ProgramFactKinds.ProcessExited, new ProcessExited(0, false, null, null, null, null), ProcessId);
            _события!.MainExited(0);
            _события.AllExited();
        }

        /// <summary>Открытый диалог программы — один и тот же, пока тест жив.</summary>
        public static DialogInfo Диалог { get; } = new(0x20, "Message", [], ["Ok to All", "Ok"], "AGDSDocParent", 1000);

        public IReadOnlyList<DialogInfo> Dialogs() => [Диалог];

        public Task<ActionResult> PressAsync(string button, CancellationToken cancellationToken) =>
            Task.FromResult(ActionResult.Failed(WindowActionFailures.PressFailed));

        // Окна у подмены нет: close срывается, и на этом проверяется код «сценарий не выполнен».
        public ActionResult Close() => ActionResult.Failed(WindowActionFailures.NoWindow);

        public Task<ActionResult> RenderAsync(CancellationToken cancellationToken) =>
            Task.FromResult(ActionResult.Failed(WindowActionFailures.NoWindow));

        public void Conclude()
        {
        }

        public void Dispose()
        {
        }
    }
}
