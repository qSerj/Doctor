using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
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
    private readonly RepairHistory repairHistory = new();
    private string? showPath;
    private ProjectAnalysis? analysis;
    private string? diagnosticSession;
    private Avalonia.Threading.DispatcherTimer? diagnosticTimer;
    private long diagnosticAfter;
    private bool diagnosticPolling;
    private bool diagnosticDegraded;

    public string TrayStatus => diagnosticSession is not null ? "Наблюдение за ProShow" :
        analysis?.Findings.Count > 0 ? "Есть рекомендации" :
        cleaner.IsProgramRunning() ? "Всё нормально" : "ProShow не запущен";

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
            Control<Button>("ProblemButton").IsVisible = true;
            Height = 520;
        }
        catch
        {
            Control<TextBlock>("ProgramStatus").Text = "Состояние ProShow неизвестно";
            Control<TextBlock>("ProgramHint").Text = "Не удалось проверить, запущен ли ProShow.";
            Control<TextBlock>("StatusIcon").Text = "?";
            Control<Button>("ProblemButton").IsVisible = true;
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
        if (!await ResolvePendingAttemptAsync())
        {
            return;
        }

        var dialog = Dialog("Решить проблему", 460, 430);
        var choices = new ListBox { ItemsSource = new[]
        {
            Choice("Проект не открывается или долго загружается", "Сначала попробуем быстро восстановить служебные файлы, затем при необходимости включим наблюдение."),
            Choice("ProShow зависает или закрывается во время работы", "Сначала попробуем быстрое восстановление, затем соберём факты работы программы."),
            Choice("Рендер зависает или ProShow закрывается", "Сначала попробуем быстрое восстановление, затем включим наблюдение за выводом."),
            Choice("Видео или картинка дают плохой результат", "Проверим проект и исходные материалы; очистка служебных файлов здесь не считается диагнозом."),
        }, SelectedIndex = 0, Height = 270 };
        var next = new Button { Content = "Далее", IsDefault = true };
        var cancel = new Button { Content = "Отмена", IsCancel = true };
        next.Click += (_, _) => dialog.Close(choices.SelectedIndex);
        cancel.Click += (_, _) => dialog.Close(-1);
        var content = new Grid
        {
            Margin = new Thickness(18),
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
        };
        content.Children.Add(new TextBlock { Text = "Что происходит?", FontSize = 17, FontWeight = FontWeight.SemiBold });
        Grid.SetRow(choices, 1);
        content.Children.Add(choices);
        var actions = Buttons(cancel, next);
        Grid.SetRow(actions, 2);
        content.Children.Add(actions);
        dialog.Content = content;
        var choice = await dialog.ShowDialog<int>(this);
        if (choice < 0) return;
        var symptom = choice switch
        {
            0 => "load",
            1 => "editing",
            2 => "render",
            _ => "media",
        };
        if (symptom == "media")
        {
            await CheckProjectAsync();
            return;
        }
        await RecoverOrObserveAsync(symptom);
    }

    private async Task<bool> ResolvePendingAttemptAsync()
    {
        var pending = repairHistory.PendingAttempt();
        if (pending is null)
        {
            return true;
        }
        if (showPath is null && pending.ProjectPath is { } previousPath && File.Exists(previousPath))
        {
            showPath = previousPath;
        }

        var answer = await AskAsync(
            "Как прошла попытка?",
            "После прошлой попытки Doctor подготовил служебные файлы заново. Проект или рендер теперь работает нормально?",
            "Да, всё работает",
            "Нет, проблема осталась");
        if (answer < 0)
        {
            return false;
        }

        repairHistory.TryAppend(new RepairHistoryEvent(
            DateTimeOffset.UtcNow,
            "feedback",
            pending.Attempt,
            pending.Symptom,
            pending.ProjectPath,
            answer == 1 ? "unresolved" : "resolved"));
        if (answer == 0)
        {
            SetResult("Хорошо. Doctor запомнил, что восстановление помогло.");
            return false;
        }
        return true;
    }

    private async Task RecoverOrObserveAsync(string symptom)
    {
        var path = showPath ?? await PickShowAsync();
        if (path is null)
        {
            return;
        }

        var failedAttempts = repairHistory.UnresolvedAttempts(symptom);
        if (failedAttempts >= 2)
        {
            var answer = await AskAsync(
                "Переходим к наблюдению",
                "Быстрое восстановление уже пробовали два раза, но проблема осталась. Теперь Doctor будет наблюдать за ProShow и сохранит факты для разбора.",
                "Начать наблюдение",
                "Отмена");
            if (answer == 0)
            {
                await StartObservationForSymptomAsync(symptom, path);
            }
            return;
        }

        if (failedAttempts == 1)
        {
            await ShowDiskCheckAsync(path);
        }

        var candidates = cleaner.Find(path);
        if (candidates.Count == 0)
        {
            await ShowInfoAsync("Быстрое восстановление", "Подходящих служебных файлов не найдено. Переходим к наблюдению за ProShow.");
            await StartObservationForSymptomAsync(symptom, path);
            return;
        }

        var answerToRepair = await AskAsync(
            "Попробовать быстрое восстановление?",
            "Doctor подготовит служебные файлы заново. Ваш проект и исходные материалы не меняются. Первый запуск после этого может быть дольше: ProShow заново проиндексирует материалы.",
            "Попробовать",
            "Наблюдать за ProShow");
        if (answerToRepair == 1)
        {
            await StartObservationForSymptomAsync(symptom, path);
            return;
        }
        if (answerToRepair != 0)
        {
            return;
        }
        if (cleaner.IsProgramRunning())
        {
            await ShowInfoAsync("Сначала закройте ProShow", "Сохраните работу и закройте ProShow обычным способом. Doctor не завершает программу принудительно.");
            return;
        }

        var attempt = repairHistory.NextAttempt();
        repairHistory.TryAppend(new RepairHistoryEvent(DateTimeOffset.UtcNow, "attempt", attempt, symptom, path));
        var outcome = cleaner.Clean(path);
        var result = outcome.ProgramRunning ? "program-running" : outcome.Failed.Count > 0 ? "partial" : "completed";
        repairHistory.TryAppend(new RepairHistoryEvent(DateTimeOffset.UtcNow, "result", attempt, symptom, path, result));
        var message = outcome.ProgramRunning
            ? "ProShow запустился во время операции. Файлы не менялись."
            : outcome.Failed.Count > 0
                ? "Часть служебных файлов не удалось подготовить. После повторной попытки откройте мастер снова."
                : "Готово. Откройте проект или повторите рендер. Если проблема останется, снова нажмите «Решить проблему» и ответьте, помогло ли.";
        SetResult(message);
        await ShowInfoAsync("Быстрое восстановление", message);
    }

    private async Task StartObservationForSymptomAsync(string symptom, string path)
    {
        if (!cleaner.IsProgramRunning())
        {
            showPath = path;
            await ShowDiagnosticAsync();
            return;
        }
        await ShowPassiveAsync(symptom == "render");
    }

    private async Task ShowDiskCheckAsync(string projectPath)
    {
        var roots = new[] { Path.GetPathRoot(projectPath), Path.GetPathRoot(ProShowLauncher.DefaultProgramPath) }
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        var lines = roots.Select(root =>
        {
            try
            {
                var drive = new DriveInfo(root!);
                return $"{drive.Name}: свободно {FormatBytes(drive.AvailableFreeSpace)} из {FormatBytes(drive.TotalSize)}";
            }
            catch (IOException) { return $"{root}: не удалось прочитать место"; }
            catch (UnauthorizedAccessException) { return $"{root}: доступ запрещён"; }
        });
        var dialog = Dialog("Проверить свободное место", 440, 300);
        var openCleanup = new Button { Content = "Открыть очистку Windows" };
        var next = new Button { Content = "Продолжить", IsDefault = true };
        openCleanup.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo("cleanmgr.exe") { UseShellExecute = true }); }
            catch (Exception error) { SetResult("Не удалось открыть очистку Windows: " + error.Message); }
        };
        next.Click += (_, _) => dialog.Close();
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(18), Spacing = 12,
            Children =
            {
                new TextBlock { Text = "Перед второй попыткой посмотрим, хватает ли места для временных файлов.", TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = string.Join("\n", lines), TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = "Очистка Windows запускается отдельно и ничего не удаляет через Doctor.", TextWrapping = TextWrapping.Wrap },
                Buttons(openCleanup, next),
            },
        };
        await dialog.ShowDialog(this);
    }

    private static string FormatBytes(long value) => value switch
    {
        >= 1_000_000_000 => $"{value / 1_000_000_000d:0.##} ГБ",
        >= 1_000_000 => $"{value / 1_000_000d:0.##} МБ",
        _ => $"{value:N0} Б",
    };

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
                : "Наблюдаем за ProShow и загрузкой проекта. Откройте проблемный проект обычным способом. Факты сохраняются локально.");
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
                if (fact.Kind == ProgramFactKinds.MainWindow)
                {
                    var title = fact.Data.TryGetProperty("title", out var titleValue) && titleValue.ValueKind == JsonValueKind.String
                        ? titleValue.GetString() : null;
                    var hung = fact.Data.TryGetProperty("hung", out var hungValue) && hungValue.ValueKind == JsonValueKind.True;
                    if (hung)
                    {
                        SetResult("ProShow не отвечает. Doctor продолжает наблюдение; факты загрузки сохраняются.");
                    }
                    else if (title is not null && title.Contains(".psh", StringComparison.OrdinalIgnoreCase))
                    {
                        SetResult($"ProShow показывает окно проекта: {title}. Наблюдение продолжается.");
                    }
                    else if (title is not null)
                    {
                        SetResult("ProShow запущен, но проект ещё не подтверждён. Doctor наблюдает загрузку.");
                    }
                }
                if (fact.Kind == ProgramFactKinds.EtwState
                    && fact.Data.TryGetProperty("state", out var state)
                    && state.GetString() is "failed" or "degraded")
                {
                    diagnosticDegraded = true;
                    SetResult("Наблюдение продолжается, но запись файловой активности неполная. Проверьте помощник диагностики.");
                }
                if (fact.Kind == ProgramFactKinds.ProcessExited
                    && fact.Data.TryGetProperty("abnormal", out var abnormal)
                    && abnormal.ValueKind == JsonValueKind.True)
                {
                    SetResult("ProShow неожиданно завершился во время загрузки. Факты сеанса сохранены.");
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

        if (cleaner.IsProgramRunning())
        {
            await ShowInfoAsync("Диагностический запуск", "ProShow уже запущен. Диагностический запуск не создаёт второй экземпляр.");
            return;
        }
        var path = showPath ?? await PickShowAsync();
        if (path is null) return;
        if (!HasObserverConfiguration())
        {
            await ShowInfoAsync("Диагностический запуск",
                "Проект выбран. Наблюдение пока не настроено: задайте адрес Observer и путь к его ключу в PSDOCTOR_OBSERVER_URL и PSDOCTOR_OBSERVER_KEY_FILE.");
            return;
        }

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

    private async Task<int> AskAsync(string title, string message, string primary, string secondary)
    {
        var dialog = Dialog(title, 440, 300);
        var first = new Button { Content = primary, IsDefault = true };
        var second = new Button { Content = secondary };
        var cancel = new Button { Content = "Отмена", IsCancel = true };
        first.Click += (_, _) => dialog.Close(0);
        second.Click += (_, _) => dialog.Close(1);
        cancel.Click += (_, _) => dialog.Close(-1);
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(18), Spacing = 12,
            Children =
            {
                new ScrollViewer { Height = 175, Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap } },
                Buttons(cancel, second, first),
            },
        };
        return await dialog.ShowDialog<int>(this);
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
