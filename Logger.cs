using System.IO;

namespace FrontSwitcher;

/// <summary>診断用の簡易ログ。%AppData%\FrontSwitcher\log.txt に追記する。</summary>
internal static class Logger
{
    private static readonly object Gate = new();
    private static string LogPath => Path.Combine(AppSettings.SettingsDirectory, "log.txt");

    public static void Log(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppSettings.SettingsDirectory);
                File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // ログ失敗は無視
        }
    }
}
