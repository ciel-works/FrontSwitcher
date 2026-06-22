using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
// WinForms/WPF 双方に同名型があるため WPF 側に固定する
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MessageBox = System.Windows.MessageBox;

namespace FrontSwitcher;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private uint _pendingModifiers;
    private uint _pendingVk;

    /// <summary>保存時に確定した設定を通知する</summary>
    public event Action<AppSettings>? SettingsSaved;

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;

        // 既存値を画面へ
        _pendingModifiers = settings.Modifiers;
        _pendingVk = settings.VirtualKey;
        HotKeyBox.Text = HotKeyText.Format(_pendingModifiers, _pendingVk);
        ProcessNameBox.Text = settings.TargetProcessName;
        ExePathBox.Text = settings.TargetExePath;
        BringToFrontCheck.IsChecked = settings.BringTargetToFront;
        MinimizeCheck.IsChecked = settings.MinimizeCurrent;
        HideTaskbarCheck.IsChecked = settings.HideFromTaskbar;
        StartupCheck.IsChecked = settings.StartWithWindows;
        foreach (var n in settings.MinimizeWithProcesses)
            MinimizeListBox.Items.Add(n);
        foreach (var p in settings.CloseTabPatterns)
            CloseTabListBox.Items.Add(p);
        UpdateTargetPanelEnabled();
        UpdateMinimizeSubOptions();
    }

    // 「最小化する」OFF のときは連動オプション（タスクバーから隠す）を無効化
    private void MinimizeCheck_Changed(object sender, RoutedEventArgs e) => UpdateMinimizeSubOptions();

    private void UpdateMinimizeSubOptions()
    {
        if (HideTaskbarCheck is not null)
            HideTaskbarCheck.IsEnabled = MinimizeCheck.IsChecked == true;
    }

    // 「最前面化する」のON/OFFで、対象アプリ入力欄の有効/無効を切り替える
    private void BringToFrontCheck_Changed(object sender, RoutedEventArgs e) => UpdateTargetPanelEnabled();

    private void UpdateTargetPanelEnabled()
    {
        if (TargetPanel is not null)
            TargetPanel.IsEnabled = BringToFrontCheck.IsChecked == true;
    }

    // --- 一緒に最小化するアプリ ---
    private void AddProcessButton_Click(object sender, RoutedEventArgs e)
    {
        if (TryAddProcess(AddProcessBox.Text))
            AddProcessBox.Clear();
    }

    private async void CaptureAddButton_Click(object sender, RoutedEventArgs e)
    {
        CaptureAddButton.IsEnabled = false;
        try
        {
            for (int sec = 3; sec >= 1; sec--)
            {
                CaptureAddButton.Content = $"{sec} 秒後...";
                await Task.Delay(1000);
            }

            IntPtr hwnd = NativeMethods.GetForegroundWindow();
            NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
            try
            {
                var proc = Process.GetProcessById((int)pid);
                TryAddProcess(proc.ProcessName);
            }
            catch
            {
                MessageBox.Show(this, "前面ウインドウのプロセスを取得できませんでした。",
                    "FrontSwitcher", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        finally
        {
            CaptureAddButton.Content = "前面から取得";
            CaptureAddButton.IsEnabled = true;
        }
    }

    private void RemoveProcessButton_Click(object sender, RoutedEventArgs e)
    {
        if (MinimizeListBox.SelectedItem is not null)
            MinimizeListBox.Items.Remove(MinimizeListBox.SelectedItem);
    }

    // --- 閉じるタブの正規表現 ---
    private void AddCloseButton_Click(object sender, RoutedEventArgs e)
    {
        if (TryAddClosePattern(AddCloseBox.Text))
            AddCloseBox.Clear();
    }

    private void RemoveCloseButton_Click(object sender, RoutedEventArgs e)
    {
        if (CloseTabListBox.SelectedItem is not null)
            CloseTabListBox.Items.Remove(CloseTabListBox.SelectedItem);
    }

    /// <summary>正規表現を検証してリストへ追加。不正・空・重複・上限を弾く。成功で true。</summary>
    private bool TryAddClosePattern(string raw)
    {
        string pat = (raw ?? "").Trim();
        if (string.IsNullOrWhiteSpace(pat))
            return false;

        // 正規表現として有効か確認
        try { _ = new Regex(pat); }
        catch (Exception ex)
        {
            MessageBox.Show(this, "正規表現が不正です:\n" + ex.Message,
                "FrontSwitcher", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        foreach (string item in CloseTabListBox.Items)
        {
            if (string.Equals(item, pat, StringComparison.Ordinal))
                return false;
        }

        if (CloseTabListBox.Items.Count >= AppSettings.MaxCloseTabPatterns)
        {
            MessageBox.Show(this, $"登録できるのは最大 {AppSettings.MaxCloseTabPatterns} 件までです。",
                "FrontSwitcher", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        CloseTabListBox.Items.Add(pat);
        return true;
    }

    /// <summary>プロセス名を正規化してリストへ追加。重複・空・上限を弾く。成功で true。</summary>
    private bool TryAddProcess(string raw)
    {
        string name = (raw ?? "").Trim();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];
        if (string.IsNullOrWhiteSpace(name))
            return false;

        // 重複（大文字小文字を無視）
        foreach (string item in MinimizeListBox.Items)
        {
            if (string.Equals(item, name, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        if (MinimizeListBox.Items.Count >= AppSettings.MaxMinimizeWith)
        {
            MessageBox.Show(this, $"登録できるのは最大 {AppSettings.MaxMinimizeWith} 件までです。",
                "FrontSwitcher", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        MinimizeListBox.Items.Add(name);
        return true;
    }

    // --- ホットキー入力 ---
    private void HotKeyBox_GotFocus(object sender, RoutedEventArgs e)
    {
        HotKeyBox.Text = "キーを押してください...";
    }

    private void HotKeyBox_LostFocus(object sender, RoutedEventArgs e)
    {
        HotKeyBox.Text = HotKeyText.Format(_pendingModifiers, _pendingVk);
    }

    private void HotKeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;

        Key key = (e.Key == Key.System) ? e.SystemKey : e.Key;

        // 修飾キー単体ならまだ確定しない（現在押下中の修飾を表示）
        if (HotKeyText.IsModifierKey(key))
        {
            HotKeyBox.Text = ModifierPreview(Keyboard.Modifiers) + "...";
            return;
        }

        // Esc は入力キャンセル
        if (key == Key.Escape)
        {
            HotKeyBox.Text = HotKeyText.Format(_pendingModifiers, _pendingVk);
            return;
        }

        var (mods, vk) = HotKeyText.FromWpf(key, Keyboard.Modifiers);
        if (vk == 0)
            return;

        _pendingModifiers = mods;
        _pendingVk = vk;
        HotKeyBox.Text = HotKeyText.Format(mods, vk);
    }

    private static string ModifierPreview(ModifierKeys mods)
    {
        string s = "";
        if ((mods & ModifierKeys.Control) != 0) s += "Ctrl + ";
        if ((mods & ModifierKeys.Alt) != 0) s += "Alt + ";
        if ((mods & ModifierKeys.Shift) != 0) s += "Shift + ";
        if ((mods & ModifierKeys.Windows) != 0) s += "Win + ";
        return s;
    }

    // --- 前面ウインドウからプロセス名を取得 ---
    private async void CaptureButton_Click(object sender, RoutedEventArgs e)
    {
        CaptureButton.IsEnabled = false;
        try
        {
            for (int sec = 3; sec >= 1; sec--)
            {
                CaptureButton.Content = $"{sec} 秒後に取得...";
                await Task.Delay(1000);
            }

            IntPtr hwnd = NativeMethods.GetForegroundWindow();
            uint pid;
            NativeMethods.GetWindowThreadProcessId(hwnd, out pid);
            try
            {
                var proc = Process.GetProcessById((int)pid);
                ProcessNameBox.Text = proc.ProcessName;
                try
                {
                    string? path = proc.MainModule?.FileName;
                    if (!string.IsNullOrEmpty(path))
                        ExePathBox.Text = path;
                }
                catch
                {
                    // 権限不足等で取得できない場合はプロセス名のみ
                }
            }
            catch
            {
                MessageBox.Show(this, "前面ウインドウのプロセスを取得できませんでした。",
                    "FrontSwitcher", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        finally
        {
            CaptureButton.Content = "前面ウインドウから取得";
            CaptureButton.IsEnabled = true;
        }
    }

    // --- exe 参照 ---
    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "対象アプリの実行ファイルを選択",
            Filter = "実行ファイル (*.exe)|*.exe|すべてのファイル (*.*)|*.*",
        };
        if (!string.IsNullOrWhiteSpace(ExePathBox.Text))
        {
            try { dlg.InitialDirectory = Path.GetDirectoryName(ExePathBox.Text); } catch { }
        }
        if (dlg.ShowDialog(this) == true)
        {
            ExePathBox.Text = dlg.FileName;
            if (string.IsNullOrWhiteSpace(ProcessNameBox.Text))
                ProcessNameBox.Text = Path.GetFileNameWithoutExtension(dlg.FileName);
        }
    }

    // --- 保存 / キャンセル ---
    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingVk == 0)
        {
            MessageBox.Show(this, "ショートカットキーを設定してください。",
                "FrontSwitcher", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        bool bringToFront = BringToFrontCheck.IsChecked == true;
        // 対象アプリの指定が要るのは「最前面化する」場合だけ
        if (bringToFront
            && string.IsNullOrWhiteSpace(ProcessNameBox.Text)
            && string.IsNullOrWhiteSpace(ExePathBox.Text))
        {
            MessageBox.Show(this, "「対象アプリを最前面に出す」が ON です。対象アプリ（プロセス名または exe パス）を設定してください。",
                "FrontSwitcher", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _settings.Modifiers = _pendingModifiers;
        _settings.VirtualKey = _pendingVk;
        _settings.BringTargetToFront = bringToFront;
        _settings.TargetProcessName = ProcessNameBox.Text.Trim();
        _settings.TargetExePath = ExePathBox.Text.Trim();
        _settings.MinimizeCurrent = MinimizeCheck.IsChecked == true;
        _settings.HideFromTaskbar = HideTaskbarCheck.IsChecked == true;
        _settings.StartWithWindows = StartupCheck.IsChecked == true;
        _settings.MinimizeWithProcesses = MinimizeListBox.Items.Cast<string>().ToList();
        _settings.CloseTabPatterns = CloseTabListBox.Items.Cast<string>().ToList();

        SettingsSaved?.Invoke(_settings);
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
