using Application = System.Windows.Application;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using WpfMessageBox = System.Windows.MessageBox;

namespace IlMatto.Desktop;

public partial class App : Application
{
    public App()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
    }

    private void OnStartup(object sender, StartupEventArgs e)
    {
        try
        {
            MainWindow = new ManagerWindow();
            MainWindow.Show();
        }
        catch (Exception ex)
        {
            ShowStartupError(ex);
            Shutdown(1);
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        WriteStartupLog(e.Exception);
        WpfMessageBox.Show(e.Exception.ToString(), "IlMatto 启动/运行错误", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception) WriteStartupLog(exception);
    }

    private static void ShowStartupError(Exception exception)
    {
        WriteStartupLog(exception);
        WpfMessageBox.Show(exception.ToString(), "IlMatto 启动失败", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private static void WriteStartupLog(Exception exception)
    {
        try
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IlMatto");
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "startup.log"), $"[{DateTime.Now:O}]\n{exception}\n\n");
        }
        catch { }
    }
}
