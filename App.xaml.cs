using System.Windows;
using System.Windows.Threading;
using System.IO;

namespace AIUsageChecker;

public partial class App : Application
{
    public App()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            WriteCrashLog(args.ExceptionObject as Exception ?? new Exception(args.ExceptionObject?.ToString()));
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        WriteCrashLog(e.Exception);
        MessageBox.Show(
            "予期しないエラーが発生しました。再起動しても続く場合は、ログを確認してください。\n\n" + LogPath,
            "AI Usage Checker",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
        Shutdown(1);
    }

    private static string LogPath
    {
        get
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AIUsageChecker");
            Directory.CreateDirectory(directory);
            return Path.Combine(directory, "crash.log");
        }
    }

    private static void WriteCrashLog(Exception exception)
    {
        try
        {
            File.AppendAllText(LogPath, $"[{DateTimeOffset.Now:O}]\n{exception}\n\n");
        }
        catch
        {
            // Logging must never hide the original failure.
        }
    }
}
