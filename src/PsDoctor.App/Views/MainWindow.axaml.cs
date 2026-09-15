using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace PsDoctor.App.Views;

public sealed partial class MainWindow : Window
{
    public MainWindow() => AvaloniaXamlLoader.Load(this);
}
