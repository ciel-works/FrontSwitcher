using System.Diagnostics;
using Microsoft.Win32;

namespace FrontSwitcher;

/// <summary>
/// Windows 起動時の自動起動を管理する。
/// 本アプリは管理者権限で動作するため、ログオン時に UAC を出さずに昇格起動できるよう
/// 「最上位の特権で実行」のタスク スケジューラ登録を使う
/// （HKCU の Run キーでは、要管理者アプリの起動時に毎回 UAC が出てしまうため）。
/// </summary>
internal static class StartupManager
{
    private const string TaskName = "FrontSwitcher";

    // 旧方式（Run キー）。残っていると二重起動・UAC の原因になるため掃除する。
    private const string LegacyRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string LegacyValueName = "FrontSwitcher";

    public static void Apply(bool enable)
    {
        RemoveLegacyRunKey();

        if (enable)
            CreateTask();
        else
            DeleteTask();
    }

    /// <summary>自動起動タスクが登録されているか</summary>
    public static bool IsEnabled()
    {
        return RunSchtasks($"/Query /TN \"{TaskName}\"") == 0;
    }

    private static void CreateTask()
    {
        string exe = Environment.ProcessPath ?? "";
        if (string.IsNullOrEmpty(exe))
            return;

        // /SC ONLOGON＝ログオン時, /RL HIGHEST＝最上位の特権（昇格・UAC なし）, /F＝既存を上書き
        string args = $"/Create /TN \"{TaskName}\" /TR \"\\\"{exe}\\\"\" /SC ONLOGON /RL HIGHEST /F";
        int code = RunSchtasks(args);
        if (code != 0)
            Logger.Log($"スタートアップ タスクの作成に失敗しました (exit={code})");
    }

    private static void DeleteTask()
    {
        if (IsEnabled())
            RunSchtasks($"/Delete /TN \"{TaskName}\" /F");
    }

    /// <summary>schtasks.exe を非表示で実行し、終了コードを返す</summary>
    private static int RunSchtasks(string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe", arguments)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return -1;
            p.WaitForExit(10000);
            return p.HasExited ? p.ExitCode : -1;
        }
        catch (Exception ex)
        {
            Logger.Log("schtasks 実行エラー: " + ex.Message);
            return -1;
        }
    }

    private static void RemoveLegacyRunKey()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(LegacyRunKey, writable: true);
            if (key?.GetValue(LegacyValueName) is not null)
                key.DeleteValue(LegacyValueName, throwOnMissingValue: false);
        }
        catch
        {
            // 掃除の失敗は無視
        }
    }
}
