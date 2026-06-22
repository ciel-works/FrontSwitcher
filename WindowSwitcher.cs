using System.Diagnostics;
using System.IO;

namespace FrontSwitcher;

/// <summary>
/// ウインドウの切替ロジック本体。動作は設定で2通り。
/// ・最小化のみ(既定): 1回目=前面ウインドウを最小化／2回目=その最小化ウインドウを復元
/// ・最前面化(BringTargetToFront=true): 1回目=最小化＋対象アプリを前面（未起動なら起動）／2回目=元へ戻す
/// </summary>
public sealed class WindowSwitcher
{
    /// <summary>退避（最小化）したウインドウ一覧。元前面ウインドウ＋一緒に最小化した登録アプリ。</summary>
    private readonly List<IntPtr> _stashed = new();

    /// <summary>元の前面ウインドウ（復元時に最前面へ戻す）。</summary>
    private IntPtr _stashedPrimary = IntPtr.Zero;

    /// <summary>
    /// ホットキー押下時に呼ばれる。設定に従って切替を行う。
    /// </summary>
    public async Task SwitchAsync(AppSettings settings)
    {
        // 対象アプリを前面化しない「最小化のみ」モード
        if (!settings.BringTargetToFront)
        {
            await SwitchMinimizeOnlyAsync(settings);
            return;
        }

        await SwitchBringToFrontAsync(settings);
    }

    /// <summary>最小化のみモード。押すたびに「最小化」⇔「復元」をトグルする。</summary>
    private async Task SwitchMinimizeOnlyAsync(AppSettings settings)
    {
        // 直前に自分が最小化したウインドウがまだ最小化中なら、まとめて復元（トグル）
        if (IsStashActive())
        {
            RestoreStashedWindow();
            return;
        }

        Minimize(settings, IntPtr.Zero);
        await CloseTabsAsync(settings);
    }

    /// <summary>最前面化モード（従来動作）。</summary>
    private async Task SwitchBringToFrontAsync(AppSettings settings)
    {
        IntPtr foreground = NativeMethods.GetForegroundWindow();
        IntPtr target = FindTargetWindow(settings);

        // 対象が既に前面にある（=直前に切替済み）なら、退避したウインドウを元に戻す
        if (target != IntPtr.Zero && foreground == target)
        {
            // 対象を引っ込めてから退避ウインドウを戻す
            if (settings.MinimizeCurrent && NativeMethods.IsWindow(target))
                NativeMethods.ShowWindow(target, NativeMethods.SW_MINIMIZE);
            RestoreStashedWindow();
            return;
        }

        // 対象ウインドウが見つからなければ exe を起動して待つ
        if (target == IntPtr.Zero)
        {
            target = await LaunchAndWaitAsync(settings);
            if (target == IntPtr.Zero)
            {
                ShowTrayBalloon("対象アプリのウインドウを見つけられませんでした。設定を確認してください。");
                return;
            }
        }

        // 前面ウインドウ＋登録アプリを最小化してから対象を前面化
        Minimize(settings, target);
        BringToFront(target);
        await CloseTabsAsync(settings);
    }

    /// <summary>設定された正規表現にマッチするブラウザタブを閉じる（Chrome/Edge）。</summary>
    private async Task CloseTabsAsync(AppSettings settings)
    {
        if (settings.CloseTabPatterns is null || settings.CloseTabPatterns.Count == 0)
            return;
        try
        {
            // UI Automation は時間がかかるためバックグラウンドで実行（UIを固めない）
            int n = await Task.Run(() => BrowserTabCloser.CloseMatchingTabs(settings.CloseTabPatterns));
            if (n > 0)
                ShowTrayBalloon($"一致したタブを {n} 個閉じました。");
        }
        catch (Exception ex)
        {
            Logger.Log("CloseTabsAsync error: " + ex.Message);
        }
    }

    /// <summary>
    /// 前面ウインドウと「一緒に最小化するアプリ」を最小化して退避する。
    /// excludeTarget は最小化対象から除外する（最前面化モードの対象アプリ自身）。
    /// </summary>
    private void Minimize(AppSettings settings, IntPtr excludeTarget)
    {
        _stashed.Clear();
        _stashedPrimary = IntPtr.Zero;

        // 元の前面ウインドウ
        IntPtr foreground = NativeMethods.GetForegroundWindow();
        if (settings.MinimizeCurrent
            && foreground != IntPtr.Zero
            && foreground != excludeTarget
            && NativeMethods.IsWindow(foreground))
        {
            MinimizeOne(foreground, settings);
            _stashedPrimary = foreground;
            _stashed.Add(foreground);
        }

        // 一緒に最小化する登録アプリ（開いているウインドウだけ対象）
        foreach (string name in settings.MinimizeWithProcesses)
        {
            foreach (IntPtr hwnd in FindWindowsOfProcess(name))
            {
                if (hwnd == excludeTarget) continue;
                if (_stashed.Contains(hwnd)) continue;
                if (NativeMethods.IsIconic(hwnd)) continue; // 既に最小化済みは触らない
                MinimizeOne(hwnd, settings);
                _stashed.Add(hwnd);
            }
        }
    }

    private static void MinimizeOne(IntPtr hWnd, AppSettings settings)
    {
        NativeMethods.ShowWindow(hWnd, NativeMethods.SW_MINIMIZE);
        if (settings.HideFromTaskbar)
            TaskbarButton.Hide(hWnd);
    }

    /// <summary>退避ウインドウのうち、まだ最小化中のものがあるか（トグル判定用）</summary>
    private bool IsStashActive()
    {
        foreach (IntPtr h in _stashed)
            if (NativeMethods.IsWindow(h) && NativeMethods.IsIconic(h))
                return true;
        return false;
    }

    /// <summary>退避していたウインドウがあるか（トレイメニューの判定用）</summary>
    public bool HasStashedWindow
    {
        get
        {
            foreach (IntPtr h in _stashed)
                if (NativeMethods.IsWindow(h)) return true;
            return false;
        }
    }

    /// <summary>
    /// 退避していたウインドウをまとめて元に戻す（消していたタスクバーボタンも復活）。
    /// 最後に「元の前面ウインドウ」を最前面にする。ホットキーのトグルからも、
    /// トレイメニュー「隠したウインドウを戻す」からも使う。戻すものが無ければ false。
    /// </summary>
    public bool RestoreStashedWindow()
    {
        bool restored = false;

        // 元前面以外（一緒に最小化した登録アプリ）を先に復元
        foreach (IntPtr h in _stashed)
        {
            if (h == _stashedPrimary) continue;
            if (NativeMethods.IsWindow(h))
            {
                TaskbarButton.Show(h);
                if (NativeMethods.IsIconic(h))
                    NativeMethods.ShowWindow(h, NativeMethods.SW_RESTORE);
                restored = true;
            }
        }

        // 最後に元の前面ウインドウを最前面へ
        if (_stashedPrimary != IntPtr.Zero && NativeMethods.IsWindow(_stashedPrimary))
        {
            TaskbarButton.Show(_stashedPrimary);
            BringToFront(_stashedPrimary);
            restored = true;
        }

        _stashed.Clear();
        _stashedPrimary = IntPtr.Zero;
        return restored;
    }

    /// <summary>指定プロセス名の、最小化対象となるトップレベル可視ウインドウを列挙する</summary>
    private static List<IntPtr> FindWindowsOfProcess(string processName)
    {
        var result = new List<IntPtr>();
        string name = (processName ?? "").Trim();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];
        if (string.IsNullOrWhiteSpace(name)) return result;

        var pids = new HashSet<uint>();
        foreach (var p in Process.GetProcessesByName(name))
        {
            try { pids.Add((uint)p.Id); } catch { }
        }
        if (pids.Count == 0) return result;

        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (!NativeMethods.IsWindowVisible(hwnd)) return true;
            NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
            if (!pids.Contains(pid)) return true;
            if (NativeMethods.GetWindow(hwnd, NativeMethods.GW_OWNER) != IntPtr.Zero) return true; // 所有される子は除外
            long ex = NativeMethods.GetWindowLongValue(hwnd, NativeMethods.GWL_EXSTYLE);
            if ((ex & NativeMethods.WS_EX_TOOLWINDOW) != 0) return true; // ツールウインドウ除外
            if (NativeMethods.GetWindowTextLength(hwnd) == 0) return true; // タイトル無しは除外
            result.Add(hwnd);
            return true;
        }, IntPtr.Zero);

        return result;
    }

    /// <summary>設定のプロセス名（無ければ exe 名）で対象ウインドウを探す</summary>
    private static IntPtr FindTargetWindow(AppSettings settings)
    {
        string name = ResolveProcessName(settings);
        if (string.IsNullOrWhiteSpace(name))
            return IntPtr.Zero;

        foreach (var p in Process.GetProcessesByName(name))
        {
            try
            {
                if (p.MainWindowHandle != IntPtr.Zero && NativeMethods.IsWindow(p.MainWindowHandle))
                    return p.MainWindowHandle;
            }
            catch
            {
                // アクセスできないプロセスは無視
            }
        }
        return IntPtr.Zero;
    }

    private static string ResolveProcessName(AppSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.TargetProcessName))
            return settings.TargetProcessName.Trim();
        if (!string.IsNullOrWhiteSpace(settings.TargetExePath))
            return Path.GetFileNameWithoutExtension(settings.TargetExePath);
        return "";
    }

    /// <summary>exe を起動し、ウインドウが現れるまで最大 ~6 秒待つ</summary>
    private static async Task<IntPtr> LaunchAndWaitAsync(AppSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.TargetExePath) || !File.Exists(settings.TargetExePath))
            return IntPtr.Zero;

        try
        {
            var psi = new ProcessStartInfo(settings.TargetExePath)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(settings.TargetExePath) ?? "",
            };
            Process.Start(psi);
        }
        catch
        {
            return IntPtr.Zero;
        }

        // プロセス名でポーリング（ブラウザ等は別プロセスに引き継ぐため名前で探す方が確実）
        for (int i = 0; i < 60; i++)
        {
            await Task.Delay(100);
            IntPtr h = FindTargetWindow(settings);
            if (h != IntPtr.Zero)
                return h;
        }
        return IntPtr.Zero;
    }

    /// <summary>SetForegroundWindow の制約を回避しつつ確実に前面化する</summary>
    private static void BringToFront(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero || !NativeMethods.IsWindow(hWnd))
            return;

        if (NativeMethods.IsIconic(hWnd))
            NativeMethods.ShowWindow(hWnd, NativeMethods.SW_RESTORE);
        else
            NativeMethods.ShowWindow(hWnd, NativeMethods.SW_SHOW);

        // フォアグラウンドスレッドに入力を一時アタッチして前面化を許可させる
        uint foreThread = NativeMethods.GetWindowThreadProcessId(NativeMethods.GetForegroundWindow(), out _);
        uint targetThread = NativeMethods.GetWindowThreadProcessId(hWnd, out _);
        uint currentThread = NativeMethods.GetCurrentThreadId();

        bool attached1 = false, attached2 = false;
        try
        {
            if (foreThread != currentThread)
                attached1 = NativeMethods.AttachThreadInput(currentThread, foreThread, true);
            if (targetThread != currentThread && targetThread != foreThread)
                attached2 = NativeMethods.AttachThreadInput(currentThread, targetThread, true);

            NativeMethods.BringWindowToTop(hWnd);
            NativeMethods.SetForegroundWindow(hWnd);
        }
        finally
        {
            if (attached1) NativeMethods.AttachThreadInput(currentThread, foreThread, false);
            if (attached2) NativeMethods.AttachThreadInput(currentThread, targetThread, false);
        }
    }

    private static void ShowTrayBalloon(string message)
    {
        TrayBalloonRequested?.Invoke(message);
    }

    /// <summary>トレイ通知を出したいときに App 側で購読する</summary>
    public static event Action<string>? TrayBalloonRequested;
}
