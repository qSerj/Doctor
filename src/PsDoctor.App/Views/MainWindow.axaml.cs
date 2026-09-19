using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using PsDoctor.Infrastructure.Observation;
using System.Runtime.Versioning;

namespace PsDoctor.App.Views;

[SupportedOSPlatform("windows")]
public sealed partial class MainWindow : Window
{
    private readonly ProShowCacheCleaner cleaner = new();
    private string? showPath;

    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
        Opened += (_, _) => RefreshStatus();
        Activated += (_, _) => RefreshStatus();
    }

    public void RefreshStatus()
    {
        var status = this.FindControl<TextBlock>("ProgramStatus")!;
        var hint = this.FindControl<TextBlock>("ProgramHint")!;
        try
        {
            var running = cleaner.IsProgramRunning();
            status.Text = running ? "ProShow работает" : "ProShow не запущен";
            hint.Text = running
                ? "Перед исправлением сохраните работу и закройте ProShow. Doctor не закроет его сам."
                : "Можно исправить типичные сбои. Проект при этом не меняется.";
        }
        catch (Exception error)
        {
            status.Text = "Состояние ProShow неизвестно";
            hint.Text = "Не удалось проверить запущенные процессы: " + error.Message;
        }
    }

    private async void ВыбратьПроект(object? sender, RoutedEventArgs args)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Выберите проект ProShow",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Проекты ProShow") { Patterns = ["*.psh"] }],
        });
        if (files.Count == 0)
        {
            return;
        }

        showPath = files[0].Path.LocalPath;
        this.FindControl<TextBlock>("ProjectPath")!.Text = showPath;
        this.FindControl<TextBlock>("ResultText")!.Text = string.Empty;
    }

    private async void ИсправитьСбои(object? sender, RoutedEventArgs args)
    {
        var result = this.FindControl<TextBlock>("ResultText")!;
        RefreshStatus();
        try
        {
            if (cleaner.IsProgramRunning())
            {
                result.Text = "Сначала сохраните работу и закройте ProShow. Затем нажмите кнопку ещё раз.";
                return;
            }

            var candidates = cleaner.Find(showPath);
            if (candidates.Count == 0)
            {
                result.Text = showPath is null
                    ? "Кэш ProShow не найден. Выберите проект, чтобы проверить также его кэш."
                    : "Кэши для исправления не найдены.";
                return;
            }

            if (!await ConfirmAsync(candidates))
            {
                return;
            }

            var outcome = cleaner.Clean(showPath);
            var lines = new List<string>();
            if (outcome.Saved.Count > 0)
            {
                lines.Add($"Сохранено кэшей: {outcome.Saved.Count}. ProShow создаст новые при следующем запуске.");
                lines.AddRange(outcome.Saved.Select(item => "• " + item.OriginalPath + " → " + item.SavedPath));
            }
            if (outcome.ProgramRunning)
            {
                lines.Add("ProShow был запущен во время исправления. Остальные файлы не тронуты.");
            }
            if (outcome.Failed.Count > 0)
            {
                lines.AddRange(outcome.Failed.Select(item => "Не удалось сохранить: " + item.Path));
            }
            result.Text = lines.Count > 0 ? string.Join(Environment.NewLine, lines) : "Кэши уже отсутствуют.";
        }
        catch (Exception error)
        {
            result.Text = "Не удалось проверить кэши: " + error.Message;
        }
        RefreshStatus();
    }

    private async Task<bool> ConfirmAsync(IReadOnlyList<ProShowCacheFile> candidates)
    {
        var dialog = new Window
        {
            Title = "Исправить типичные сбои",
            Width = 520,
            Height = 340,
            MinWidth = 460,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };
        var move = new Button { Content = "Сохранить кэши и продолжить", IsDefault = true };
        var cancel = new Button { Content = "Отмена", IsCancel = true };
        move.Click += (_, _) => dialog.Close(true);
        cancel.Click += (_, _) => dialog.Close(false);
        dialog.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(20),
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = "Doctor переименует эти кэши:", FontWeight = Avalonia.Media.FontWeight.SemiBold },
                new ScrollViewer
                {
                    Height = 200,
                    Content = new TextBlock
                    {
                        Text = string.Join(Environment.NewLine, candidates.Select(item => "• " + item.Path)),
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    },
                },
                new TextBlock { Text = "Старые файлы останутся рядом. ProShow должен быть закрыт.", TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new StackPanel
                {
                    Orientation = Avalonia.Layout.Orientation.Horizontal,
                    Spacing = 8,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                    Children = { cancel, move },
                },
            },
        };
        return await dialog.ShowDialog<bool>(this);
    }
}
