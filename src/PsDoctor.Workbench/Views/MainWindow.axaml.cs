using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using PsDoctor.Workbench.ViewModels;

namespace PsDoctor.Workbench.Views;

/// <summary>
/// Окно пульта. Кроме показа и передачи нажатий тут ничего нет: состояние живёт
/// в <see cref="WorkbenchViewModel"/>, и оттого проверяется тестами без интерфейса.
/// </summary>
public sealed partial class MainWindow : Window
{
    private readonly WorkbenchViewModel model = new();

    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
        DataContext = model;
        // Адрес и ключ уже заданы переменными окружения — подключаемся сами: пульт открывают, чтобы
        // смотреть на стенд, а не чтобы каждый раз нажимать одну и ту же кнопку.
        Opened += async (_, _) =>
        {
            if (model.Address.Length > 0 && model.KeyFile.Length > 0)
            {
                await model.ConnectAsync();
            }
        };
    }

    private async void Подключиться(object? sender, RoutedEventArgs e) => await model.ConnectAsync();

    private async void Выполнить(object? sender, RoutedEventArgs e) => await model.RunAsync();

    private async void Отменить(object? sender, RoutedEventArgs e) => await model.CancelAsync();

    private async void Прекратить(object? sender, RoutedEventArgs e) => await model.StopAsync();

    private async void Запустить(object? sender, RoutedEventArgs e) => await model.LaunchAsync();

    private async void ЗакрытьПрограмму(object? sender, RoutedEventArgs e) => await model.CloseProgramAsync();

    private async void РендерБезДиалогов(object? sender, RoutedEventArgs e) => await model.RunDiagnosticAsync(false);

    private async void РендерСДиалогом(object? sender, RoutedEventArgs e) => await model.RunDiagnosticAsync(true);

    private async void СобратьПакет(object? sender, RoutedEventArgs e) => await model.ExportAsync();

    private async void ВыбратьКаталогОбмена(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Каталог обмена",
            AllowMultiple = false,
        });
        if (folders.Count == 1)
        {
            model.ExchangeDirectory = folders[0].Path.LocalPath;
        }
    }

    private async void ПодключитьсяКПрограмме(object? sender, RoutedEventArgs e) => await model.AttachAsync();

    private async void Сделано(object? sender, RoutedEventArgs e) => await model.ConfirmAsync();

    private async void ОпытВыбран(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox { SelectedItem: ExperimentRow experiment })
        {
            await model.OpenExperimentAsync(experiment);
        }
    }

    private void ОбновитьОпыты(object? sender, RoutedEventArgs e) => model.LoadExperiments();

    private async void ОбновитьСеансы(object? sender, RoutedEventArgs e) => await model.RefreshSessionsAsync();

    private async void ОбновитьДиалоги(object? sender, RoutedEventArgs e) => await model.RefreshDialogsAsync();

    private async void ПрименитьФильтр(object? sender, RoutedEventArgs e) => await model.SelectAsync(model.SelectedSession);

    private async void СеансВыбран(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox { SelectedItem: SessionRow row } && row.Id != model.SelectedSession?.Id)
        {
            await model.SelectAsync(row);
        }
    }

    private async void НажатьКнопкуДиалога(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Content: string button })
        {
            await model.PressAsync(button);
        }
    }
}
