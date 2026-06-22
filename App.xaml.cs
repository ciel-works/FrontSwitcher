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
        bool firstRun = !System.IO.File.Exists(AppSettings.SettingsPath);
        Settings = AppSettings.Load();
        _switcher = new WindowSwitcher();

        WindowSwitcher.TrayBalloonRequested += ShowBalloon;

        InitTrayIcon();
        InitHotKeys();

        // 初回起動時はガイドとして設定画面を開く（ホットキー設定のため）
        if (firstRun)
        {
            ShowBalloon("FrontSwitcher を常駐しました。トレイアイコンからホットキー等を設定できます。");
            OpenSettings();
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
