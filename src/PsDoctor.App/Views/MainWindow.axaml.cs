using System.Diagnostics;
using System.Globalization;
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
using PsDoctor.Infrastructure.Installation;
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
    private bool diagnosticCrashed;
    private DateTime diagnosticStarted;

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
        bool? running;
        try { running = cleaner.IsProgramRunning(); }
        catch { running = null; }
        var watching = diagnosticSession is not null;
        var hasFindings = analysis?.Findings.Count > 0;

        var (tone, icon, title, hint) =
            watching ? ("watch", "IconWatch", "Идёт наблюдение", "Работайте в ProShow как обычно. Doctor записывает, что происходит.")
            : hasFindings ? ("warn", "IconWarning", "Есть рекомендации", "В проекте есть файлы, которые могут замедлять работу ProShow.")
            : running is null ? ("idle", "IconUnknown", "Состояние неизвестно", "Не удалось проверить, запущен ли ProShow.")
            : running.Value
                ? analysis is null
                    ? ("ok", "IconOk", "ProShow работает", "Можно работать. Проект ещё не проверен.")
                    : ("ok", "IconOk", "Всё хорошо!", "Известных помех в проекте нет. Можно работать.")
                : ("idle", "IconIdle", "ProShow не запущен", "Doctor на месте и готов помочь.");
        SetTone(Control<Border>("Hero"), tone);
        Control<PathIcon>("StatusIcon").Data = Glyph(icon);
        Control<TextBlock>("ProgramStatus").Text = title;
        Control<TextBlock>("ProgramHint").Text = hint;
        Control<Button>("FinishButton").IsVisible = watching;
        UpdateNextStep();
    }

    /// <summary>Синей бывает одна карточка — следующий шаг: закончить наблюдение, открыть, проверить или показать рекомендации.</summary>
    private void UpdateNextStep()
    {
        var free = diagnosticSession is null && !(analysis?.Findings.Count > 0);
        Control<Button>("OpenButton").Classes.Set("primary", free && (showPath is null || analysis is not null));
        Control<Button>("CheckButton").Classes.Set("primary", free && showPath is not null && analysis is null);
    }

    private static readonly string[] Tones = ["ok", "warn", "error", "watch", "info", "idle"];

    private static void SetTone(Control control, string tone)
    {
        foreach (var name in Tones) control.Classes.Set(name, name == tone);
    }

    private static Geometry? Glyph(string key) =>
        Application.Current!.TryGetResource(key, null, out var value) ? value as Geometry : null;

    private static string ToneIcon(string tone) => tone switch
    {
        "ok" => "IconOk",
        "warn" => "IconWarning",
        "error" => "IconError",
        "watch" => "IconWatch",
        _ => "IconInfo",
    };

    private void SetResult(string message, string tone = "info", bool busy = false)
    {
        var border = Control<Border>("ResultBorder");
        Control<TextBlock>("ResultText").Text = message;
        Control<PathIcon>("ResultIcon").Data = Glyph(ToneIcon(tone));
        Control<ProgressBar>("ResultProgress").IsVisible = busy;
        SetTone(border, tone);
        border.IsVisible = !string.IsNullOrWhiteSpace(message);
    }

    private void SetAdvice(string message, string tone, string icon, bool showFindings)
    {
        var border = Control<Border>("AdviceBorder");
        border.IsVisible = true;
        SetTone(border, tone);
        Control<PathIcon>("AdviceIcon").Data = Glyph(icon);
        Control<TextBlock>("AdviceText").Text = message;
        Control<Button>("ShowFindingsButton").IsVisible = showFindings;
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
        if (!File.Exists(path)) { SetResult("Файл проекта не найден.", "error"); return null; }
        showPath = path;

        analysis = null;
        Control<Border>("ProjectLine").IsVisible = true;
        Control<TextBlock>("ProjectName").Text = Path.GetFileNameWithoutExtension(path);
        Control<TextBlock>("ProjectPath").Text = path;
        ToolTip.SetTip(Control<TextBlock>("ProjectPath"), path);
        Control<TextBlock>("ProjectResolution").IsVisible = false;
        Control<Border>("AdviceBorder").IsVisible = false;
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
            SetResult("Проект отправлен в ProShow. Если он не открылся, откройте его в ProShow вручную.");
        }
        catch (Exception error)
        {
            SetResult("Не удалось открыть проект: " + error.Message, "error");
        }
        RefreshStatus();
    }

    private async void ОткрытьПроект(object? sender, RoutedEventArgs args) => await OpenProjectAsync();

    public async Task CheckProjectAsync()
    {
        var path = showPath ?? await PickShowAsync();
        if (path is null) return;
        var checkButton = Control<Button>("CheckButton");
        checkButton.IsEnabled = false;
        SetResult("Проверяем проект…", busy: true);
        try
        {
            var result = await Task.Run(() => ProjectAnalysis.Analyze(path));
            if (showPath != path) return;
            analysis = result;
            var resolution = Control<TextBlock>("ProjectResolution");
            resolution.Text = "Разрешение показа: " + result.Resolution;
            resolution.IsVisible = result.Resolution is not null;
            if (result.Findings.Count == 0)
            {
                SetAdvice("Проверка завершена: известных помех не найдено.", "ok", "IconOk", false);
            }
            else
            {
                SetAdvice($"Найдено рекомендаций: {result.Findings.Count}. Работать с проектом можно и так.", "warn", "IconAdvice", true);
            }
            SetResult("");
        }
        catch (Exception error)
        {
            SetResult("Не удалось проверить проект: " + error.Message, "error");
        }
        finally
        {
            checkButton.IsEnabled = true;
        }
        RefreshStatus();
    }

    private async void ПроверитьПроект(object? sender, RoutedEventArgs args) => await CheckProjectAsync();

    private async void ПоказатьРекомендации(object? sender, RoutedEventArgs args)
    {
        if (analysis is null || showPath is null) return;
        var list = new StackPanel { Spacing = 8 };
        foreach (var finding in analysis.Findings) list.Children.Add(FindingCard(finding));
        var dialog = Dialog("Рекомендации по проекту", 520);
        var close = DialogButton("Понятно", primary: true);
        close.IsCancel = true;
        close.Click += (_, _) => dialog.Close();
        dialog.Content = Page("IconAdvice", "warn", "Рекомендации по проекту",
            $"{Path.GetFileNameWithoutExtension(showPath)} · найдено: {analysis.Findings.Count}. Работать с проектом можно и так — это подсказки, а не ошибки.",
            new ScrollViewer { MaxHeight = 400, Content = list },
            Buttons(close));
        await dialog.ShowDialog(this);
    }

    private static Border FindingCard(Finding finding)
    {
        var text = new StackPanel { Spacing = 2 };
        text.Children.Add(new TextBlock { Text = FindingTitle(finding), FontWeight = FontWeight.SemiBold, FontSize = 15, TextWrapping = TextWrapping.Wrap });
        text.Children.Add(new TextBlock { Text = FindingFile(finding), Classes = { "caption" }, TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(new TextBlock { Text = FindingDetail(finding), Classes = { "body" }, Margin = new Thickness(0, 4, 0, 0) });
        Grid.SetColumn(text, 1);
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 12 };
        var tile = Tile(FindingIcon(finding), "warn");
        tile.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;
        row.Children.Add(tile);
        row.Children.Add(text);
        return new Border { Classes = { "card" }, Padding = new Thickness(12), Child = row };
    }

    private static string FindingIcon(Finding finding) => finding.RuleId switch
    {
        "oversized-stills" or "zoom-exceeds-pixels" => "IconMedia",
        "oversized-video" => "IconRender",
        "cp1251-paths" or "foreign-root" => "IconOpen",
        _ => "IconInfo",
    };

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

        var dialog = Dialog("Решить проблему", 500);
        var choices = new ListBox { Classes = { "choices" }, ItemsSource = new[]
        {
            Choice("IconSlowLoad", "Проект не открывается или долго загружается", "Сначала быстро восстановим служебные файлы, потом при необходимости включим наблюдение."),
            Choice("IconStrange", "ProShow зависает или закрывается во время работы", "Сначала быстрое восстановление, потом соберём сведения о работе программы."),
            Choice("IconRender", "Рендер зависает или ProShow закрывается", "Сначала быстрое восстановление, потом понаблюдаем за выводом."),
            Choice("IconMedia", "Видео или картинка дают плохой результат", "Проверим проект и исходные материалы."),
        }, SelectedIndex = 0 };
        var next = DialogButton("Далее", primary: true);
        var cancel = DialogButton("Отмена");
        cancel.IsCancel = true;
        next.Click += (_, _) => dialog.Close(choices.SelectedIndex);
        cancel.Click += (_, _) => dialog.Close(-1);
        choices.DoubleTapped += (_, _) => dialog.Close(choices.SelectedIndex);
        dialog.Content = Page("IconQuestion", "info", "Что происходит?", "Выберите, что больше похоже на вашу ситуацию.",
            choices, Buttons(cancel, next));
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
            "Нет, проблема осталась",
            "IconQuestion");
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
            SetResult("Отлично! Doctor запомнил, что восстановление помогло.", "ok");
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
                null,
                "IconWatch",
                "watch");
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
            await ShowInfoAsync("Быстрое восстановление", "Служебных файлов, которые стоит подготовить заново, нет. Переходим к наблюдению за ProShow.", "IconRepair");
            await StartObservationForSymptomAsync(symptom, path);
            return;
        }

        var answerToRepair = await AskAsync(
            "Попробовать быстрое восстановление?",
            "Doctor подготовит служебные файлы заново. Ваш проект и исходные материалы не меняются. Первый запуск после этого может быть дольше: ProShow заново проиндексирует материалы.",
            "Попробовать",
            "Наблюдать за ProShow",
            "IconRepair");
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
            await ShowInfoAsync("Сначала закройте ProShow", "Сохраните работу и закройте ProShow обычным способом, затем снова нажмите «Решить проблему». Doctor не закрывает программу сам.", "IconError", "error");
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
        var tone = result == "completed" ? "ok" : "warn";
        SetResult(message, tone);
        await ShowInfoAsync(result == "completed" ? "Восстановление завершено" : "Быстрое восстановление", message, ToneIcon(tone), tone);
    }

    private async Task StartObservationForSymptomAsync(string symptom, string path)
    {
        if (!cleaner.IsProgramRunning() && diagnosticSession is null)
        {
            showPath = path;
            await ShowInfoAsync("Наблюдение за ProShow",
                "ProShow сейчас не работает. Прошлый сбой записать уже нельзя: Doctor может запустить проект под наблюдением, чтобы вы повторили проблему.", "IconWatch", "watch");
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
        var dialog = Dialog("Проверить свободное место");
        var openCleanup = DialogButton("Открыть очистку Windows");
        var next = DialogButton("Продолжить", primary: true);
        openCleanup.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo("cleanmgr.exe") { UseShellExecute = true }); }
            catch (Exception error) { SetResult("Не удалось открыть очистку Windows: " + error.Message, "error"); }
        };
        next.Click += (_, _) => dialog.Close();
        dialog.Content = Page("IconDisk", "info", "Хватает ли места на диске?",
            "Перед второй попыткой посмотрим, есть ли место для временных файлов ProShow.",
            new Border { Classes = { "card" }, Child = new TextBlock { Text = string.Join("\n", lines), Classes = { "body" } } },
            new TextBlock { Text = "Очистка Windows открывается отдельно; Doctor через неё ничего не удаляет.", Classes = { "caption" }, TextWrapping = TextWrapping.Wrap },
            Buttons(openCleanup, next));
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
            await ShowInfoAsync("Наблюдение уже идёт", "Чтобы закончить его, нажмите «Закончить наблюдение» в окне Doctor.", "IconWatch", "watch");
            return;
        }
        if (!HasObserverConfiguration())
        {
            await ShowInfoAsync("Наблюдение не настроено", "Позовите администратора: Doctor установлен не полностью.", "IconError", "error");
            return;
        }
        if (!cleaner.IsProgramRunning())
        {
            await ShowInfoAsync("Наблюдение за ProShow", "ProShow сейчас не работает. Прошлый сбой записать уже нельзя: откройте проект и повторите проблему под наблюдением.", "IconWatch", "watch");
            return;
        }
        try
        {
            using var observer = CreateObserver();
            var accepted = await observer.AttachAsync(origin: SessionOrigins.Wizard);
            diagnosticSession = accepted.Session;
            StartDiagnosticWatch();
            SetResult(render
                ? "Наблюдаем за ProShow. Запустите вывод обычным способом. Всё записанное остаётся на этом компьютере."
                : "Наблюдаем за ProShow. Откройте проблемный проект обычным способом. Всё записанное остаётся на этом компьютере.", "watch");
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
            SetResult(message, "error");
        }
        catch (Exception error)
        {
            SetResult("Не удалось начать наблюдение: " + error.Message, "error");
        }
        RefreshStatus();
    }
    private void StartDiagnosticWatch()
    {
        diagnosticAfter = 0;
        diagnosticDegraded = false;
        diagnosticCrashed = false;
        diagnosticStarted = DateTime.Now;
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
                        SetResult("ProShow не отвечает. Doctor продолжает наблюдение и записывает загрузку.", "warn");
                    }
                    else if (title is not null && title.Contains(".psh", StringComparison.OrdinalIgnoreCase))
                    {
                        SetResult($"ProShow открыл проект: {title}. Наблюдение продолжается.", "watch");
                    }
                    else if (title is not null)
                    {
                        SetResult("ProShow запущен, проект загружается. Doctor наблюдает.", "watch", busy: true);
                    }
                }
                if (fact.Kind == ProgramFactKinds.EtwState
                    && fact.Data.TryGetProperty("state", out var state)
                    && state.GetString() is "failed" or "degraded")
                {
                    diagnosticDegraded = true;
                    SetResult("Наблюдение продолжается, но запись файловой активности неполная. Проверьте помощник диагностики.", "warn");
                }
                if (fact.Kind == ProgramFactKinds.ProcessExited
                    && fact.Data.TryGetProperty("abnormal", out var abnormal)
                    && abnormal.ValueKind == JsonValueKind.True)
                {
                    diagnosticCrashed = true;
                    SetResult("ProShow неожиданно закрылся. Doctor дописывает сеанс.", "error");
                }
                if (fact.Kind == ProgramFactKinds.SessionFinished)
                {
                    diagnosticSession = null;
                    diagnosticTimer?.Stop();
                    ShowObservationOutcome(diagnosticCrashed ? "ProShow неожиданно закрылся" : "ProShow закрылся");
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

    private static bool HasObserverConfiguration() => ObserverAddress() is not null;

    private static ObserverClient CreateObserver()
    {
        var (url, key) = ObserverAddress() ?? throw new InvalidOperationException("наблюдение не настроено");
        return new ObserverClient(url, key);
    }

    /// <summary>
    /// Адрес и ключ наблюдателя. Переменные окружения — для инженера и стенда, они главнее; без них — то, что
    /// оставили установщик и сторож: адрес из настроек машины, ключ из профиля. Ключа нет, пока сторож
    /// ни разу не запускался, — тогда наблюдение не настроено.
    /// </summary>
    private static (Uri Url, string Key)? ObserverAddress()
    {
        var url = Environment.GetEnvironmentVariable(ObserverConnection.UrlVariable);
        var keyFile = Environment.GetEnvironmentVariable(ObserverConnection.KeyFileVariable);
        if (Uri.TryCreate(url, UriKind.Absolute, out var variableUrl) && File.Exists(keyFile))
        {
            var variableKey = File.ReadAllText(keyFile).Trim();
            return variableKey.Length > 0 ? (variableUrl, variableKey) : null;
        }
        var layout = InstalledLayout.Current;
        var (settings, _) = layout.LoadSettings();
        return settings is not null && layout.ReadKey() is { } key ? (settings.LocalUrl, key) : null;
    }

    /// <summary>Заканчивает наблюдение: Observer перестаёт собирать факты, ProShow продолжает работать.</summary>
    public async Task FinishObservationAsync(CancellationToken cancellationToken = default)
    {
        if (diagnosticSession is not { } session) return;
        try
        {
            using var observer = CreateObserver();
            await observer.StopAsync(session, cancellationToken);
            diagnosticSession = null;
            diagnosticTimer?.Stop();
            ShowObservationOutcome(diagnosticCrashed ? "ProShow неожиданно закрылся" : "Наблюдение завершено");
        }
        catch (ObserverException error) when (error.Error?.Error == ObserverErrors.SessionFinished)
        {
            diagnosticSession = null;
            diagnosticTimer?.Stop();
            ShowObservationOutcome(diagnosticCrashed ? "ProShow неожиданно закрылся" : "Наблюдение уже завершилось");
        }
        catch (Exception error)
        {
            SetResult("Не удалось закончить наблюдение: " + error.Message, "error");
        }
        RefreshStatus();
    }

    /// <summary>
    /// Итог сеанса, как бы он ни кончился: что случилось и что дальше. Детекторов пока нет (Э4.4), поэтому
    /// «дальше» всегда одно — сеанс ждёт инженера; время начала нужно ему, чтобы найти сеанс в списке.
    /// </summary>
    private void ShowObservationOutcome(string what)
    {
        var started = diagnosticStarted.ToString("dd.MM, HH:mm", CultureInfo.GetCultureInfo("ru-RU"));
        var incomplete = diagnosticDegraded ? " Запись файловой активности неполная." : "";
        SetResult($"{what}. Всё записано — сеанс от {started}.{incomplete} Инженер разберёт его, когда подключится.",
            diagnosticCrashed ? "error" : diagnosticDegraded ? "warn" : "ok");
    }

    private async void ЗакончитьНаблюдение(object? sender, RoutedEventArgs args) => await FinishObservationAsync();

    private async Task ShowDiagnosticAsync()
    {
        if (diagnosticSession is not null)
        {
            await FinishObservationAsync();
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
            await ShowInfoAsync("Наблюдение не настроено",
                "Проект выбран, но наблюдение не настроено. Позовите администратора: Doctor установлен не полностью.", "IconError", "error");
            return;
        }

        var dialog = Dialog("Запуск проекта с диагностикой");
        var launch = DialogButton("Запустить ProShow", primary: true);
        var cancel = DialogButton("Отмена");
        cancel.IsCancel = true;
        launch.Click += (_, _) => dialog.Close(true);
        cancel.Click += (_, _) => dialog.Close(false);
        dialog.Content = Page("IconLaunch", "watch", "Запуск с диагностикой",
            "Doctor запустит ProShow и будет наблюдать, как открывается проект. Это не мешает работе.",
            ProjectCard(path),
            Buttons(cancel, launch));
        if (!await dialog.ShowDialog<bool>(this)) return;
        try
        {
            using var observer = CreateObserver();
            await observer.HealthAsync();
            var accepted = await observer.RunAsync($"launch \"{path}\"", origin: SessionOrigins.Wizard);
            diagnosticSession = accepted.Session;
            StartDiagnosticWatch();
            SetResult("Наблюдаем за ProShow. Работайте как обычно. Всё записанное остаётся на этом компьютере.", "watch");
        }
        catch (Exception error)
        {
            SetResult("Не удалось запустить наблюдение: " + error.Message, "error");
        }
        RefreshStatus();
    }

    private async Task ShowInfoAsync(string title, string message, string icon = "IconInfo", string tone = "info")
    {
        var dialog = Dialog(title);
        var close = DialogButton("Понятно", primary: true);
        close.IsCancel = true;
        close.Click += (_, _) => dialog.Close();
        dialog.Content = Page(icon, tone, title, message, Buttons(close));
        await dialog.ShowDialog(this);
    }

    /// <summary>0 — главный ответ, 1 — второй, −1 — отмена или закрытие окна.</summary>
    private async Task<int> AskAsync(string title, string message, string primary, string? secondary, string icon = "IconQuestion", string tone = "info")
    {
        var dialog = Dialog(title);
        var first = DialogButton(primary, primary: true);
        var cancel = DialogButton("Отмена");
        cancel.IsCancel = true;
        first.Click += (_, _) => dialog.Close(0);
        cancel.Click += (_, _) => dialog.Close(-1);
        var buttons = new List<Button> { cancel };
        if (secondary is not null)
        {
            var second = DialogButton(secondary);
            second.Click += (_, _) => dialog.Close(1);
            buttons.Add(second);
        }
        buttons.Add(first);
        dialog.Content = Page(icon, tone, title, message, Buttons([.. buttons]));
        return await dialog.ShowDialog<int>(this);
    }

    private static Grid Choice(string icon, string title, string description)
    {
        var text = new StackPanel { Spacing = 2, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, FontSize = 15, TextWrapping = TextWrapping.Wrap });
        text.Children.Add(new TextBlock { Text = description, Classes = { "caption" }, TextWrapping = TextWrapping.Wrap });
        Grid.SetColumn(text, 1);
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 12 };
        row.Children.Add(Tile(icon));
        row.Children.Add(text);
        return row;
    }

    private static Border ProjectCard(string path)
    {
        var text = new StackPanel { Spacing = 1, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = Path.GetFileNameWithoutExtension(path), FontWeight = FontWeight.SemiBold, FontSize = 15, TextWrapping = TextWrapping.Wrap });
        text.Children.Add(new TextBlock { Text = path, Classes = { "caption" }, TextWrapping = TextWrapping.Wrap });
        Grid.SetColumn(text, 1);
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 12 };
        row.Children.Add(Tile("IconProject"));
        row.Children.Add(text);
        return new Border { Classes = { "card" }, Padding = new Thickness(12), Child = row };
    }

    private static Border Tile(string icon, string? tone = null)
    {
        var tile = new Border { Classes = { "tile" }, Child = new PathIcon { Data = Glyph(icon) } };
        if (tone is not null) tile.Classes.Add(tone);
        return tile;
    }

    /// <summary>Страница диалога: круглый значок тона, заголовок, пояснение, затем остальное содержимое.</summary>
    private static StackPanel Page(string icon, string tone, string heading, string? text, params Control[] content)
    {
        var titles = new StackPanel { Spacing = 4, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        titles.Children.Add(new TextBlock { Text = heading, Classes = { "heading", tone } });
        if (text is not null) titles.Children.Add(new TextBlock { Text = text, Classes = { "body" } });
        Grid.SetColumn(titles, 1);
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 14 };
        header.Children.Add(new Border { Classes = { "badge", tone }, Child = new PathIcon { Data = Glyph(icon) } });
        header.Children.Add(titles);
        var page = new StackPanel { Margin = new Thickness(20), Spacing = 14 };
        page.Children.Add(header);
        foreach (var control in content) page.Children.Add(control);
        return page;
    }

    private Window Dialog(string title, double width = 460) => new()
    {
        Title = title, Width = width, SizeToContent = SizeToContent.Height, CanResize = false, Icon = Icon,
        WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false
    };

    private static Button DialogButton(string text, bool primary = false)
    {
        var button = new Button { Content = text, IsDefault = primary, MinWidth = 96, HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center };
        if (primary) button.Classes.Add("primary");
        return button;
    }

    private static StackPanel Buttons(params Button[] buttons)
    {
        var panel = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 4, 0, 0),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
        foreach (var button in buttons) panel.Children.Add(button);
        return panel;
    }
}
