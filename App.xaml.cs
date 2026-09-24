using System.Threading;
using System.Windows;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;
using WinForms = System.Windows.Forms;

namespace FrontSwitcher;

public partial class App : Application
{
    private WinForms.NotifyIcon? _trayIcon;
    private System.Drawing.Icon? _trayIconImage;
    private HotKeyService? _hotKeys;
    private MouseGestureService? _gestures;
    private WinForms.ToolStripMenuItem? _pauseGestureItem;
    private WindowSwitcher? _switcher;
    private SettingsWindow? _settingsWindow;
    private Mutex? _singleInstanceMutex;

    public AppSettings Settings { get; private set; } = new();

    private void Application_Startup(object sender, StartupEventArgs e)
    {
        // 二重起動防止
        _singleInstanceMutex = new Mutex(initiallyOwned: true, "FrontSwitcher_SingleInstance_Mutex", out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show("FrontSwitcher は既に起動しています。", "FrontSwitcher",
                MessageBoxButton.OK, MessageBoxImage.Information);
            // 所有していない Mutex を Exit で解放しないよう破棄しておく
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            Shutdown();
            return;
        }

        Logger.Log("=== App startup ===");
        // 昇格アプリでもトレイアイコンを確実に出すため、TaskbarCreated メッセージを受け取れるようにする
        AllowTaskbarCreatedMessage();
        // 旧方式(Runキー)の自動起動が残っていれば毎回掃除する（管理者アプリはRunキーから正常起動できないため）
        StartupManager.CleanLegacyStartup();

        bool firstRun = !System.IO.File.Exists(AppSettings.SettingsPath);
        Settings = AppSettings.Load();

        // 自動起動が有効なら、タスク定義を最新の内容で登録し直す（自己修復）。
        // 旧版の登録には「バッテリー駆動でタスク停止」等の条件が残っているため、起動のたびに上書きする。
        if (Settings.StartWithWindows)
            StartupManager.Apply(true);

        _switcher = new WindowSwitcher();

        WindowSwitcher.TrayBalloonRequested += ShowBalloon;

        InitTrayIcon();
        InitHotKeys();
        InitGestures();

        // 初回起動時はガイドとして設定画面を開く（ホットキー設定のため）
        if (firstRun)
        {
            ShowBalloon("FrontSwitcher を常駐しました。トレイアイコンからホットキー等を設定できます。");
            OpenSettings();
        }
    }

    /// <summary>
    /// 昇格（管理者）プロセスでも、Explorer からの "TaskbarCreated" ブロードキャストを
    /// 受け取れるようにする。これが無いと、Explorer より先に起動した場合などに
    /// トレイアイコンが表示されないことがある。
    /// </summary>
    private void AllowTaskbarCreatedMessage()
    {
        try
        {
            uint msg = NativeMethods.RegisterWindowMessage("TaskbarCreated");
            if (msg != 0)
                NativeMethods.ChangeWindowMessageFilter(msg, NativeMethods.MSGFLT_ADD);
        }
        catch (Exception ex)
        {
            Logger.Log("ChangeWindowMessageFilter 失敗: " + ex.Message);
        }
    }

    private void InitTrayIcon()
    {
        _trayIconImage = IconFactory.CreateCheckIcon();
        _trayIcon = new WinForms.NotifyIcon
        {
            Icon = _trayIconImage,
            Visible = true,
            Text = "FrontSwitcher",
        };

        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("設定...", null, (_, _) => OpenSettings());
        menu.Items.Add("今すぐ切替（テスト）", null, async (_, _) => await DoSwitchAsync());
        menu.Items.Add("隠したウインドウを戻す", null, (_, _) => RestoreHidden());
        _pauseGestureItem = new WinForms.ToolStripMenuItem("マウスジェスチャ一時停止") { CheckOnClick = true };
        _pauseGestureItem.CheckedChanged += (_, _) =>
        {
            if (_gestures is not null) _gestures.Paused = _pauseGestureItem.Checked;
            Logger.Log($"MouseGesture: 一時停止={_pauseGestureItem.Checked}");
        };
        menu.Items.Add(_pauseGestureItem);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("終了", null, (_, _) => ExitApp());
        _trayIcon.ContextMenuStrip = menu;

        _trayIcon.DoubleClick += (_, _) => OpenSettings();
    }

    private void InitHotKeys()
    {
        _hotKeys = new HotKeyService();
        _hotKeys.HotKeyPressed += async () => await DoSwitchAsync();
        RegisterCurrentHotKey();
    }

    private void InitGestures()
    {
        _gestures = new MouseGestureService();
        // フックスレッドから呼ばれるので UI スレッドへ回す
        _gestures.GestureRecognized += (binding, target) =>
            Dispatcher.BeginInvoke(new Action(async () => await DoGestureAsync(binding.Action, target)));
        ApplyGestures();
    }

    /// <summary>現在の設定でマウスジェスチャを開始／停止する</summary>
    private void ApplyGestures()
    {
        if (_gestures is null) return;
        _gestures.Apply(Settings);
        if (_pauseGestureItem is not null)
            _pauseGestureItem.Enabled = Settings.MouseGestureEnabled;
    }

    private async Task DoGestureAsync(GestureAction action, IntPtr target)
    {
        if (_switcher is null) return;
        try
        {
            switch (action)
            {
                case GestureAction.Hide:
                    await _switcher.HideAsync(Settings);
                    break;
                case GestureAction.Restore:
                    _switcher.RestoreStashedWindow();
                    break;
                case GestureAction.Switch:
                    await _switcher.SwitchAsync(Settings);
                    break;
                case GestureAction.BringTarget:
                    await _switcher.BringTargetAsync(Settings);
                    break;
                case GestureAction.MinimizeWindow:
                case GestureAction.ToggleMaximizeWindow:
                case GestureAction.CloseWindow:
                    WindowSwitcher.WindowCommand(target, action);
                    break;
            }
        }
        catch (Exception ex)
        {
            ShowBalloon($"ジェスチャの実行中にエラーが発生しました: {ex.Message}");
        }
    }

    /// <summary>現在の設定でホットキーを登録。失敗したら通知する。</summary>
    public void RegisterCurrentHotKey()
    {
        if (_hotKeys is null) return;
        bool ok = _hotKeys.Register(Settings.Modifiers, Settings.VirtualKey);
        if (!ok)
        {
            ShowBalloon($"ホットキー [{HotKeyText.Format(Settings.Modifiers, Settings.VirtualKey)}] を登録できませんでした。" +
                        "他のアプリと競合している可能性があります。設定で別のキーをお試しください。");
        }
    }

    private async Task DoSwitchAsync()
    {
        if (_switcher is null) return;
        try
        {
            Logger.Log("DoSwitch start");
            await _switcher.SwitchAsync(Settings);
            Logger.Log("DoSwitch done");
        }
        catch (Exception ex)
        {
            ShowBalloon($"切替中にエラーが発生しました: {ex.Message}");
        }
    }

    private void RestoreHidden()
    {
        if (_switcher is null) return;
        bool ok = _switcher.RestoreStashedWindow();
        ShowBalloon(ok ? "隠したウインドウを戻しました。" : "戻せる隠しウインドウはありません。");
    }

    private void OpenSettings()
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new SettingsWindow(Settings.Clone());
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.SettingsSaved += OnSettingsSaved;
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    private void OnSettingsSaved(AppSettings newSettings)
    {
        Settings = newSettings;
        Settings.Save();
        StartupManager.Apply(Settings.StartWithWindows);
        RegisterCurrentHotKey();
        ApplyGestures();
        ShowBalloon("設定を保存しました。");
    }

    private void ShowBalloon(string message)
    {
        _trayIcon?.ShowBalloonTip(4000, "FrontSwitcher", message, WinForms.ToolTipIcon.Info);
    }

    private void ExitApp()
    {
        Shutdown();
    }

    private void Application_Exit(object sender, ExitEventArgs e)
    {
        _hotKeys?.Dispose();
        _gestures?.Dispose();
        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
        }
        _trayIconImage?.Dispose();
        _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();
    }
}
