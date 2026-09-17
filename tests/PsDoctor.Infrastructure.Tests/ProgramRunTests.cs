using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using PsDoctor.Core.Observation;
using PsDoctor.Core.Scenarios;
using PsDoctor.Infrastructure.Observation;
using Xunit;

namespace PsDoctor.Infrastructure.Tests;

/// <summary>
/// Окна куста и действия с ними на настоящем окне Windows PowerShell с диалогом <c>#32770</c>, без ProShow.
/// Окна видны только с рабочего стола, поэтому тест выполняется в сеансе пользователя (на стенде — задачей
/// планировщика), а в нулевом сеансе и не на Windows молча не выполняется.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ProgramRunTests
{
    private static readonly TimeSpan Терпение = TimeSpan.FromSeconds(60);

    // Окно формы, над ним диалог MessageBox с владельцем-формой и двумя кнопками. Нажатая кнопка
    // пишется в заголовок формы — так тест видит, какую нажали; вторая кнопка закрывает форму.
    private const string Скрипт = """
        Add-Type -AssemblyName System.Windows.Forms
        $form = New-Object System.Windows.Forms.Form
        $form.Text = 'psdoctor-проба'
        $form.Add_Shown({
            $answer = [System.Windows.Forms.MessageBox]::Show($form, 'текст диалога', 'psdoctor-диалог', 'OKCancel')
            $form.Text = "psdoctor-нажато-$answer"
        })
        [void]$form.ShowDialog()
        """;

    private sealed class События : IProgramEvents
    {
        public ConcurrentQueue<string?> Заголовки { get; } = new();

        public ConcurrentQueue<DialogInfo> Диалоги { get; } = new();

        public TaskCompletionSource ВсеВышли { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void MainExited(int? exitCode)
        {
        }

        public void AllExited() => ВсеВышли.TrySetResult();

        public void TitleChanged(string? title) => Заголовки.Enqueue(title);

        public void DialogAppeared(DialogInfo dialog) => Диалоги.Enqueue(dialog);

        public void DialogDisappeared(long handle)
        {
        }

        public void Activity(bool quiet)
        {
        }
    }

    [Fact]
    public async Task Диалог_виден_фактом_нажимается_без_курсора_и_окно_закрывается()
    {
        if (!OperatingSystem.IsWindows() || Process.GetCurrentProcess().SessionId == 0)
        {
            return;
        }
        var часы = Stopwatch.StartNew();
        var журнал = new FactLog(new FactJournalWriter(new StringWriter(), "тест", () => часы.Elapsed));
        var события = new События();
        var powershell = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
        var закодирован = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(Скрипт));

        using var запуск = ProgramRun.Start(powershell, $"\"{powershell}\" -NoProfile -EncodedCommand {закодирован}", null, журнал, события);
        try
        {
            var диалог = await Дождаться(() => события.Диалоги.FirstOrDefault(d => d.Title == "psdoctor-диалог"));
            Assert.Equal("#32770", диалог.Class);
            Assert.Contains("текст диалога", диалог.Texts);
            Assert.Equal(2, диалог.Buttons.Count);
            Assert.Contains(события.Заголовки, t => t == "psdoctor-проба");
            Assert.Equal(диалог.Handle, Assert.Single(запуск.Dialogs()).Handle);

            // Кнопки подписаны языком системы: нажимается первая, как её прочитал опрос.
            var нажатие = await запуск.PressAsync(диалог.Buttons[0], CancellationToken.None);

            Assert.True(нажатие.Succeeded, нажатие.Reason);
            var нажато = Данные<DialogPressed>(журнал.After(0).Single(f => f.Kind == ProgramFactKinds.DialogPressed));
            Assert.Equal("invoked", нажато.Result);
            Assert.False(нажато.CursorMoved, "нажатие сдвинуло курсор");
            await Дождаться(() => события.Заголовки.FirstOrDefault(t => t == "psdoctor-нажато-OK"));
            Assert.Contains(журнал.After(0), f => f.Kind == ProgramFactKinds.DialogClosed);
            Assert.Empty(запуск.Dialogs());

            Assert.True(запуск.Close().Succeeded);
            await события.ВсеВышли.Task.WaitAsync(Терпение);
            Assert.Contains(журнал.After(0), f => f.Kind == ProgramFactKinds.CloseRequested);
            Assert.Contains(журнал.After(0), f => f.Kind == ProgramFactKinds.JobActivity);
        }
        finally
        {
            try
            {
                using var программа = Process.GetProcessById(запуск.ProcessId);
                программа.Kill(entireProcessTree: true);
            }
            catch (ArgumentException)
            {
            }
        }
    }

    [Fact]
    public async Task Нажатие_без_диалога_и_закрытие_без_окна_срываются()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        var часы = Stopwatch.StartNew();
        var журнал = new FactLog(new FactJournalWriter(new StringWriter(), "тест", () => часы.Elapsed));
        var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");

        using var запуск = ProgramRun.Start(cmd, $"\"{cmd}\" /c \"ping -n 3 127.0.0.1 >nul\"", null, журнал, new События());

        Assert.Equal(WindowActionFailures.NoDialog, (await запуск.PressAsync("ОК", CancellationToken.None)).Reason);
        Assert.Equal(WindowActionFailures.NoWindow, запуск.Close().Reason);
    }

    private static T Данные<T>(Fact факт) => факт.Data.Deserialize<T>(ObservationJson.Options)!;

    private static async Task<T> Дождаться<T>(Func<T?> найти)
        where T : class
    {
        using var время = new CancellationTokenSource(Терпение);
        while (true)
        {
            if (найти() is { } найдено)
            {
                return найдено;
            }
            await Task.Delay(100, время.Token);
        }
    }
}
