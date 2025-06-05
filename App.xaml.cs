using System.Windows;
using Wpf.Ui.Appearance;

namespace AppInstaller
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Apply the system theme (auto-detects Windows light/dark mode)
            ApplicationThemeManager.ApplySystemTheme();
        }
    }
}