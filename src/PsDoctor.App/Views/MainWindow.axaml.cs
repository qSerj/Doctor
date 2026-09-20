using System.Diagnostics;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using PsDoctor.Core.Rules;
using PsDoctor.Core.Observation;
using PsDoctor.Infrastructure.Observation;
using PsDoctor.Observer.Client;

namespace PsDoctor.App.Views;

[SupportedOSPlatform("windows")]
public sealed partial class MainWindow : Window
{
    private readonly ProShowCacheCleaner cleaner = new();
    private string? showPath;
    private ProjectAnalysis? analysis;
    private string? diagnosticSession;
    private Avalonia.Threading.DispatcherTimer? diagnosticTimer;
    private long diagnosticAfter;
    private bool diagnosticPolling;
    private bool diagnosticDegraded;

    public string TrayStatus => diagnosticSession is not null ? "Наблюдение за ProShow" :
        analysis?.Findings.Count > 0 ? "Есть рекомендации" :
        Control<TextBlock>("FooterStatus").Text == "● ProShow запущен" ? "Всё нормально" : "ProShow не запущен";

    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
        Opened += (_, _) => RefreshStatus();
        Activated += (_, _) => RefreshStatus();
        Closed += (_, _) => diagnosticTimer?.Stop();
    }

    private T Control<T>(string name) where T : Control => this.FindControl<T>(name)!;

    public void RefreshStatus()
    {
        try
        {
            var running = cleaner.IsProgramRunning();
            var hasFindings = analysis?.Findings.Count > 0;
            Control<TextBlock>("ProgramStatus").Text = diagnosticSession is not null ? "Наблюдаем за ProShow" : hasFindings ? "Есть рекомендации к выбранному проекту"
                : running ? analysis is null ? "ProShow работает" : "Всё хорошо!"
                : "ProShow не запущен";
            Control<TextBlock>("ProgramHint").Text = diagnosticSession is not null ? "Работайте как обычно. Факты сохраняются локально." : hasFindings
                ? "В выбранном файле найдены моменты, которые могут замедлять работу ProShow."
                : running ? analysis is null ? "Можно работать. Проект ещё не проверен." : "В выбранном проекте нет известных рекомендаций. Можно работать."
                : "Запустите ProShow или откройте проект через Doctor.";
            Control<TextBlock>("StatusIcon").Text = hasFindings ? "!" : running ? "✓" : "○";
            Control<TextBlock>("StatusIcon").Foreground = Brush.Parse(hasFindings ? "#DC9700" : running ? "#39A45A" : "#687788");
            Control<TextBlock>("FooterStatus").Text = running ? "● ProShow запущен" : "● ProShow не запущен";
            Control<Button>("ProblemButton").IsVisible = running;
            Control<Button>("DiagnosticButton").IsVisible = diagnosticSession is not null || !running;
            Control<Button>("DiagnosticButton").IsEnabled = diagnosticSession is not null || HasObserverConfiguration();
            Control<Button>("DiagnosticButton").Content = diagnosticSession is not null ? "Закончить наблюдение" : HasObserverConfiguration() ? "Запустить проект с диагностикой" : "Диагностика не настроена";
            Control<Button>("RepairButton").IsVisible = !running;
            Height = running ? 475 : 520;
        }
        catch
        {
            Control<TextBlock>("ProgramStatus").Text = "Состояние ProShow неизвестно";
            Control<TextBlock>("ProgramHint").Text = "Не удалось проверить, запущен ли ProShow.";
            Control<TextBlock>("StatusIcon").Text = "?";
            Control<TextBlock>("FooterStatus").Text = "Состояние неизвестно";
            Control<Button>("ProblemButton").IsVisible = true;
            Control<Button>("DiagnosticButton").IsVisible = false;
            Control<Button>("RepairButton").IsVisible = false;
        }
    }

    private void SetResult(string message)
    {
        var result = Control<TextBlock>("ResultText");
        result.Text = message;
        result.IsVisible = !string.IsNullOrWhiteSpace(message);
    }

    private async Task<string?> PickShowAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Выберите проект ProShow",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Проекты ProShow") { Patterns = ["*.psh"] }],
        });
        if (files.Count == 0) return null;
        var path = files[0].Path.LocalPath;
        if (!File.Exists(path)) { SetResult("Файл проекта не найден."); return null; }
        showPath = path;

        analysis = null;
        Control<TextBlock>("ProjectLabel").Text = "Выбранный проект";
        Control<TextBlock>("ProjectName").Text = Path.GetFileName(path);
        Control<TextBlock>("ProjectPath").Text = path;
        Control<TextBlock>("ProjectResolution").Text = "Разрешение: неизвестно";
        Control<TextBlock>("AdviceText").Text = "Совет: проверьте проект, чтобы увидеть рекомендации.";
        Control<Button>("ShowFindingsButton").IsVisible = false;
        RefreshStatus();
        return path;
    }

    public async Task OpenProjectAsync()
    {
        var path = await PickShowAsync();
        if (path is null) return;
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            SetResult("Проект передан ProShow обычным способом. Выбор проекта в Doctor пока не подтверждает, что он открыт в ProShow.");
        }
        catch (Exception error)
        {
            SetResult("Не удалось открыть проект: " + error.Message);
        }
        RefreshStatus();
    }

    private async void ОткрытьПроект(object? sender, RoutedEventArgs args) => await OpenProjectAsync();

    public async Task CheckProjectAsync()
    {
        var path = showPath ?? await PickShowAsync();
        if (path is null) return;
        SetResult("Проверяем проект…");
        try
        {
            var result = await Task.Run(() => ProjectAnalysis.Analyze(path));
            if (showPath != path) return;
            analysis = result;
            Control<TextBlock>("ProjectResolution").Text = result.Resolution is null
                ? "Разрешение: неизвестно" : "Разрешение: " + result.Resolution;
            Control<TextBlock>("AdviceText").Text = result.Findings.Count == 0
                ? "Проверка завершена. По доступным данным рекомендаций нет."
                : $"Найдено рекомендаций: {result.Findings.Count}. Работа с проектом не блокируется.";
            Control<Button>("ShowFindingsButton").IsVisible = result.Findings.Count > 0;
            SetResult("");
        }
        catch (Exception error)
        {
            SetResult("Не удалось проверить проект: " + error.Message);
        }
        RefreshStatus();
    }

    private async void ПроверитьПроект(object? sender, RoutedEventArgs args) => await CheckProjectAsync();

    private async void ПоказатьРекомендации(object? sender, RoutedEventArgs args)
    {
        if (analysis is null || showPath is null) return;
        var lines = analysis.Findings.Select((finding, index) =>
            $"{index + 1}. {FindingTitle(finding)}\n{FindingFile(finding)}\n{FindingDetail(finding)}");
        await ShowInfoAsync($"Рекомендации: {Path.GetFileName(showPath)}",
            $"{analysis.Findings.Count} рекомендаций\n\n" + string.Join("\n\n", lines));
    }

    private static string FindingTitle(Finding finding) => finding.RuleId switch
    {
        "oversized-stills" => "Изображение крупнее, чем нужно для показа",
        "zoom-exceeds-pixels" => "При увеличении может не хватить чёткости",
        "oversized-video" => "Используется лишь часть видео",
        "cp1251-paths" => "Файл проекта не найден по записанному пути",
        "foreign-root" => "Проект ссылается на каталог с другого компьютера",
        "audio-longer-than-show" => "Звуковая дорожка длиннее показа",
        _ => "Возможная особенность проекта",
    };

    private static string FindingFile(Finding finding) =>
        finding.Address.Media is { } media ? Path.GetFileName(media.Raw) : finding.Address.Name ?? "Проект";

    private static string FindingDetail(Finding finding) => finding.RuleId switch
    {
        "oversized-stills" => $"Ширина файла {Number(finding, "currentWidthPx")} пикселей, для этого показа достаточно около {Number(finding, "targetWidthPx")}. Большой файл может занимать лишнюю память.",
        "zoom-exceeds-pixels" => $"При увеличении нужно {Number(finding, "neededWidthPx")} пикселей по ширине, у файла {Number(finding, "actualWidthPx")}. Изображение может выглядеть размытым.",
        "oversized-video" => $"Используется {Number(finding, "usedMs") / 1000} с из {Number(finding, "videoLengthMs") / 1000} с видео. Объём исходника может замедлять работу.",
        "cp1251-paths" => $"Файл используется {Number(finding, "referenceCount")} раз, но не найден по записанному пути.",
        "foreign-root" => "В файле шоу записан путь с другого компьютера. Проверьте доступность файлов.",
        "audio-longer-than-show" => "Часть звуковой дорожки остаётся за пределами показа.",
        _ => "Проверьте этот объект в проекте.",
    };

    private static long Number(Finding finding, string key) =>
        finding.Numbers.TryGetValue(key, out var value) ? value : 0;

    public async Task ShowProblemAsync()
    {
        var dialog = Dialog("Разобраться с проблемой", 430, 290);
        var choices = new ListBox { ItemsSource = new[]
        {
            Choice("Проект долго открывается или не открывается", "Doctor проследит за новым запуском проекта."),
            Choice("В ProShow что-то ведёт себя странно", "Doctor будет наблюдать за открытой программой и файловой активностью."),
            Choice("Рендер зависает или ProShow закрывается", "Doctor запишет факты, пока вы сами запускаете вывод."),
        }, SelectedIndex = 0 };
        var next = new Button { Content = "Далее", IsDefault = true };
        var cancel = new Button { Content = "Отмена", IsCancel = true };
        next.Click += (_, _) => dialog.Close(choices.SelectedIndex);
        cancel.Click += (_, _) => dialog.Close(-1);
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(18), Spacing = 12,
            Children = { new TextBlock { Text = "Что происходит?", FontSize = 17, FontWeight = FontWeight.SemiBold },
                choices, Buttons(cancel, next) }
        };
        var choice = await dialog.ShowDialog<int>(this);
        if (choice < 0) return;
        if (choice == 0) await ShowDiagnosticAsync();
        else await ShowPassiveAsync(choice == 2);
    }

    private async Task ShowPassiveAsync(bool render)
    {
        if (diagnosticSession is not null)
        {
            await ShowInfoAsync("Наблюдение за ProShow", "Наблюдение уже идёт. Чтобы закончить его, нажмите «Закончить наблюдение».");
            return;
        }
        if (!HasObserverConfiguration())
        {
            await ShowInfoAsync("Наблюдение за ProShow", "Наблюдение не настроено. Укажите адрес Observer и путь к ключу в PSDOCTOR_OBSERVER_URL и PSDOCTOR_OBSERVER_KEY_FILE.");
            return;
        }
        if (!cleaner.IsProgramRunning())
        {
            await ShowInfoAsync("Наблюдение за ProShow", "ProShow сейчас не работает. Прошлый сбой записать уже нельзя: откройте проект и повторите проблему под наблюдением.");
            return;
        }
        try
        {
            using var observer = CreateObserver();
            var accepted = await observer.AttachAsync();
            diagnosticSession = accepted.Session;
            StartDiagnosticWatch();
            SetResult(render
                ? "Наблюдаем за ProShow и файловой активностью. Запустите вывод обычным способом. Факты сохраняются локально."
                : "Наблюдаем за ProShow и файловой активностью. Работайте как обычно. Факты сохраняются локально.");
        }
        catch (ObserverException error)
        {
            var message = error.Error?.Error switch
            {
                ObserverErrors.ProgramNotRunning => "ProShow уже закрылся. Прошлый сбой записать нельзя; повторите проблему под наблюдением.",
                ObserverErrors.AmbiguousProgram => "Найдено несколько окон ProShow. Закройте лишние экземпляры и повторите подключение.",
                ObserverErrors.EtwUnavailable => "Не удалось включить полную диагностику файловой активности. Проверьте помощник наблюдения.",
                ObserverErrors.ProgramRunning => "Наблюдение уже идёт в другом сеансе.",
                _ => "Не удалось начать наблюдение: " + error.Message,
            };
            SetResult(message);
        }
        catch (Exception error)
        {
            SetResult("Не удалось начать наблюдение: " + error.Message);
        }
        RefreshStatus();
    }
    private void StartDiagnosticWatch()
    {
        diagnosticAfter = 0;
        diagnosticDegraded = false;
        diagnosticTimer?.Stop();
        diagnosticTimer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        diagnosticTimer.Tick += async (_, _) => await PollDiagnosticAsync();
        diagnosticTimer.Start();
    }

    private async Task PollDiagnosticAsync()
    {
        if (diagnosticPolling || diagnosticSession is not { } session) return;
        diagnosticPolling = true;
        try
        {
            using var observer = CreateObserver();
            await foreach (var fact in observer.ReadFactsAsync(session, diagnosticAfter))
            {
                if (diagnosticSession != session) return;
                diagnosticAfter = fact.Number;
                if (fact.Kind == ProgramFactKinds.EtwState
                    && fact.Data.TryGetProperty("state", out var state)
                    && state.GetString() is "failed" or "degraded")
                {
                    diagnosticDegraded = true;
                    SetResult("Наблюдение продолжается, но запись файловой активности неполная. Проверьте помощник диагностики.");
                }
                if (fact.Kind == ProgramFactKinds.SessionFinished)
                {
                    diagnosticSession = null;
                    diagnosticTimer?.Stop();
                    SetResult(diagnosticDegraded
                        ? "ProShow закрылся. Факты сохранены, но запись файловой активности неполная."
                        : "ProShow закрылся. Факты наблюдения сохранены.");
                    RefreshStatus();
                    return;
                }
            }
        }
        catch (Exception)
        {
            // Краткий обрыв связи не завершает сеанс: следующий опрос продолжит с последнего факта.
        }
        finally { diagnosticPolling = false; }
    }
    private async void РазобратьсяСПроблемой(object? sender, RoutedEventArgs args) => await ShowProblemAsync();

    private static bool HasObserverConfiguration() =>
        Uri.TryCreate(Environment.GetEnvironmentVariable(ObserverConnection.UrlVariable), UriKind.Absolute, out _)
        && File.Exists(Environment.GetEnvironmentVariable(ObserverConnection.KeyFileVariable));

    private static ObserverClient CreateObserver()
    {
        var url = Environment.GetEnvironmentVariable(ObserverConnection.UrlVariable)!;
        var keyFile = Environment.GetEnvironmentVariable(ObserverConnection.KeyFileVariable)!;
        return new ObserverClient(new Uri(url), File.ReadAllText(keyFile).Trim());
    }

    private async Task ShowDiagnosticAsync()
    {
        if (diagnosticSession is not null)
        {
            try
            {
                using var observer = CreateObserver();
                await observer.StopAsync(diagnosticSession);
                diagnosticSession = null;
                diagnosticTimer?.Stop();
                SetResult(diagnosticDegraded ? "Наблюдение завершено. Факты сохранены, запись файловой активности неполная." : "Наблюдение завершено. Факты сеанса сохранены.");
            }
            catch (ObserverException error) when (error.Error?.Error == ObserverErrors.SessionFinished)
            {
                diagnosticSession = null;
                diagnosticTimer?.Stop();
                SetResult("Наблюдение уже завершилось. Записанные факты сохранены.");
            }
            catch (Exception error)
            {
                SetResult("Не удалось закончить наблюдение: " + error.Message);
            }
            RefreshStatus();
            return;
        }

        if (!HasObserverConfiguration())
        {
            await ShowInfoAsync("Диагностический запуск",
                "Наблюдение пока не настроено. Следующий шаг: задать адрес Observer и путь к его ключу в PSDOCTOR_OBSERVER_URL и PSDOCTOR_OBSERVER_KEY_FILE.");
            return;
        }
        if (cleaner.IsProgramRunning())
        {
            await ShowInfoAsync("Диагностический запуск", "ProShow уже запущен. Диагностический запуск не создаёт второй экземпляр.");
            return;
        }
        var path = showPath ?? await PickShowAsync();
        if (path is null) return;

        var dialog = Dialog("Запуск проекта с диагностикой", 440, 290);
        var launch = new Button { Content = "Запустить ProShow", IsDefault = true };
        var cancel = new Button { Content = "Отмена", IsCancel = true };
        launch.Click += (_, _) => dialog.Close(true);
        cancel.Click += (_, _) => dialog.Close(false);
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(18), Spacing = 12,
            Children = {
                new TextBlock { Text = "Doctor запустит ProShow и будет наблюдать, как открывается проект. Это поможет понять, если что-то пойдёт не так.", TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = Path.GetFileName(path), FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = path, TextWrapping = TextWrapping.Wrap },
                Buttons(cancel, launch)
            }
        };
        if (!await dialog.ShowDialog<bool>(this)) return;
        try
        {
            using var observer = CreateObserver();
            await observer.HealthAsync();
            var accepted = await observer.RunAsync($"launch \"{path}\"");
            diagnosticSession = accepted.Session;
            StartDiagnosticWatch();
            SetResult("Наблюдаем за ProShow. Работайте как обычно. Факты сохраняются локально.");
        }
        catch (Exception error)
        {
            SetResult("Не удалось запустить наблюдение: " + error.Message);
        }
        RefreshStatus();
    }

    private async void ДиагностическийЗапуск(object? sender, RoutedEventArgs args) => await ShowDiagnosticAsync();

    public async Task RepairAsync()
    {
        try
        {
            if (cleaner.IsProgramRunning())
            {
                await ShowInfoAsync("Исправить типичные сбои", "Сохраните работу и закройте ProShow вручную. Пока он запущен, временные файлы не меняются.");
                return;
            }
            var candidates = cleaner.Find(showPath);
            if (candidates.Count == 0)
            {
                await ShowInfoAsync("Исправить типичные сбои", "Временные файлы для исправления не найдены.");
                return;
            }
            if (!await ConfirmAsync(candidates)) return;
            var outcome = cleaner.Clean(showPath);
            var summary = outcome.ProgramRunning ? "ProShow запущен. Дальнейшая очистка остановлена."
                : outcome.Saved.Count > 0 ? $"Временные файлы сохранены рядом с исходными: {outcome.Saved.Count}."
                : "Файлы уже отсутствуют.";
            if (outcome.Failed.Count > 0) summary += $" Не удалось сохранить: {outcome.Failed.Count}.";
            SetResult(summary);
            await ShowInfoAsync("Исправление завершено", summary);
        }
        catch (Exception error)
        {
            SetResult("Не удалось исправить типичные сбои: " + error.Message);
        }
        RefreshStatus();
    }

    private async void ИсправитьСбои(object? sender, RoutedEventArgs args) => await RepairAsync();

    private async Task<bool> ConfirmAsync(IReadOnlyList<ProShowCacheFile> candidates)
    {
        var dialog = Dialog("Исправить типичные сбои", 440, 360);
        var confirm = new Button { Content = "Исправить", IsDefault = true };
        var cancel = new Button { Content = "Отмена", IsCancel = true };
        confirm.Click += (_, _) => dialog.Close(true);
        cancel.Click += (_, _) => dialog.Close(false);
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(18), Spacing = 10,
            Children =
            {
                new TextBlock { Text = "Doctor сохранит временные файлы, которые иногда мешают работе. ProShow должен быть закрыт.", TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = $"Найдено файлов: {candidates.Count}. Проектных: {candidates.Count(x => x.Kind == ProShowCacheKind.Project)}, общих: {candidates.Count(x => x.Kind == ProShowCacheKind.Program)}.", TextWrapping = TextWrapping.Wrap },
                new ScrollViewer { Height = 140, Content = new TextBlock { Text = string.Join("\n", candidates.Select(x => x.Path)), TextWrapping = TextWrapping.Wrap } },
                new TextBlock { Text = "Старые файлы останутся рядом под резервными именами.", TextWrapping = TextWrapping.Wrap },
                Buttons(cancel, confirm),
            }
        };
        return await dialog.ShowDialog<bool>(this);
    }

    private async Task ShowInfoAsync(string title, string message)
    {
        var dialog = Dialog(title, 440, 300);
        var close = new Button { Content = "Закрыть", IsDefault = true };
        close.Click += (_, _) => dialog.Close();
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(18), Spacing = 12,
            Children = { new ScrollViewer { Height = 205, Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap } }, Buttons(close) }
        };
        await dialog.ShowDialog(this);
    }

    private static StackPanel Choice(string title, string description) => new()
    {
        Spacing = 2,
        Children =
        {
            new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap },
            new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Brush.Parse("#596579") }
        }
    };
    private static Window Dialog(string title, double width, double height) => new()
    {
        Title = title, Width = width, Height = height, MinWidth = 380,
        WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false
    };

    private static StackPanel Buttons(params Button[] buttons)
    {
        var panel = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
        foreach (var button in buttons) panel.Children.Add(button);
        return panel;
    }
}
