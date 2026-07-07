using System.Diagnostics;
using System.IO;
using System.Security;
using System.Text;
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

    /// <summary>旧方式(Runキー)の自動起動登録を消す（起動のたびに呼ぶ）</summary>
    public static void CleanLegacyStartup() => RemoveLegacyRunKey();

    private static void CreateTask()
    {
        string exe = Environment.ProcessPath ?? "";
        if (string.IsNullOrEmpty(exe))
            return;

        // schtasks のコマンドラインでは電源条件を指定できず、既定で
        // 「バッテリー駆動になったらタスクを停止」「バッテリー中は開始しない」
        // 「72時間で強制終了」が付いてしまう（ノートPCで常駐が突然消える原因）。
        // そのため XML 定義で明示的に無効化して登録する。
        string user = Environment.UserDomainName + "\\" + Environment.UserName;
        string xml = $"""
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <Triggers>
    <LogonTrigger>
      <Enabled>true</Enabled>
      <Delay>PT15S</Delay>
      <UserId>{SecurityElement.Escape(user)}</UserId>
    </LogonTrigger>
  </Triggers>
  <Principals>
    <Principal id="Author">
      <UserId>{SecurityElement.Escape(user)}</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>HighestAvailable</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <StartWhenAvailable>false</StartWhenAvailable>
    <Enabled>true</Enabled>
  </Settings>
  <Actions Context="Author">
    <Exec>
      <Command>{SecurityElement.Escape("\"" + exe + "\"")}</Command>
    </Exec>
  </Actions>
</Task>
""";

        string tmp = Path.Combine(Path.GetTempPath(), "FrontSwitcherTask.xml");
        try
        {
            File.WriteAllText(tmp, xml, Encoding.Unicode);
            int code = RunSchtasks($"/Create /TN \"{TaskName}\" /XML \"{tmp}\" /F");
            if (code != 0)
                Logger.Log($"スタートアップ タスクの作成に失敗しました (exit={code})");
            else
                Logger.Log("スタートアップ タスクを登録/更新しました（電源条件なし）");
        }
        catch (Exception ex)
        {
            Logger.Log("スタートアップ タスク作成エラー: " + ex.Message);
        }
        finally
        {
            try { File.Delete(tmp); } catch { }
        }
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
