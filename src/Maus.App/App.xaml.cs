using System.Windows;

namespace Maus.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Thème Fluent de Windows 11, clair ou sombre selon le système.
#pragma warning disable WPF0001
        ThemeMode = ThemeMode.System;
#pragma warning restore WPF0001
        base.OnStartup(e);
    }
}
