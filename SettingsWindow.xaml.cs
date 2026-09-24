using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
// WinForms/WPF 双方に同名型があるため WPF 側に固定する
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MessageBox = System.Windows.MessageBox;
using ComboBoxItem = System.Windows.Controls.ComboBoxItem;

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

        // マウスジェスチャ
        GestureEnableCheck.IsChecked = settings.MouseGestureEnabled;
        foreach (var g in settings.MouseGestures)
            GestureListBox.Items.Add(g.Clone());
        foreach (GestureAction a in Enum.GetValues<GestureAction>())
            GestureActionCombo.Items.Add(new ComboBoxItem { Content = GestureBinding.ActionToText(a), Tag = a });
        GestureActionCombo.SelectedIndex = 0;
        GestureStartBox.Text = settings.GestureStartDistance.ToString();
        GestureStrokeBox.Text = settings.GestureStrokeDistance.ToString();
        foreach (var n in settings.GestureExcludeProcesses)
            GestureExcludeListBox.Items.Add(n);
        UpdateGesturePatternText();
    }

    // --- マウスジェスチャ ---
    private string _pendingPattern = "";

    private void GestureDirButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: string dir }) return;
        // 同じ向きが続くのは1ストロークなので追加しない
        if (_pendingPattern.Length > 0 && _pendingPattern[^1] == dir[0]) return;
        if (_pendingPattern.Length >= GestureBinding.MaxStrokes) return;
        _pendingPattern += dir;
        UpdateGesturePatternText();
    }

    private void GestureClearButton_Click(object sender, RoutedEventArgs e)
    {
        _pendingPattern = "";
        UpdateGesturePatternText();
    }

    private void UpdateGesturePatternText()
    {
        bool empty = _pendingPattern.Length == 0;
        GesturePatternText.Text = empty ? "(未入力)" : GestureBinding.PatternToArrows(_pendingPattern);
        GesturePatternText.Foreground = empty ? System.Windows.Media.Brushes.Gray : System.Windows.Media.Brushes.Black;
    }

    private void AddGestureButton_Click(object sender, RoutedEventArgs e)
    {
        if (!GestureBinding.IsValidPattern(_pendingPattern))
        {
            MessageBox.Show(this, "向きのボタン（↑↓←→）でジェスチャを入力してください。",
                "FrontSwitcher", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (GestureActionCombo.SelectedItem is not ComboBoxItem { Tag: GestureAction action }) return;

        uint mods = 0;
        if (GestureCtrlCheck.IsChecked == true) mods |= HotKeyService.MOD_CONTROL;
        if (GestureShiftCheck.IsChecked == true) mods |= HotKeyService.MOD_SHIFT;
        if (GestureAltCheck.IsChecked == true) mods |= HotKeyService.MOD_ALT;
        if (GestureWinCheck.IsChecked == true) mods |= HotKeyService.MOD_WIN;

        var dup = GestureListBox.Items.Cast<GestureBinding>()
            .FirstOrDefault(g => g.Modifiers == mods && g.Pattern == _pendingPattern);
        if (dup is not null)
        {
            MessageBox.Show(this, $"同じキーと向きのジェスチャが既にあります:\n{dup}",
                "FrontSwitcher", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (GestureListBox.Items.Count >= AppSettings.MaxGestures)
        {
            MessageBox.Show(this, $"登録できるのは最大 {AppSettings.MaxGestures} 件までです。",
                "FrontSwitcher", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        GestureListBox.Items.Add(new GestureBinding(mods, _pendingPattern, action));
        _pendingPattern = "";
        UpdateGesturePatternText();
    }

    private void RemoveGestureButton_Click(object sender, RoutedEventArgs e)
    {
        if (GestureListBox.SelectedItem is not null)
            GestureListBox.Items.Remove(GestureListBox.SelectedItem);
    }

    private void AddExcludeButton_Click(object sender, RoutedEventArgs e)
    {
        if (TryAddProcess(GestureExcludeListBox, AppSettings.MaxGestureExclude, AddExcludeBox.Text))
            AddExcludeBox.Clear();
    }

    private async void CaptureExcludeButton_Click(object sender, RoutedEventArgs e) =>
        await CaptureProcessIntoAsync(CaptureExcludeButton, GestureExcludeListBox, AppSettings.MaxGestureExclude);

    private void RemoveExcludeButton_Click(object sender, RoutedEventArgs e)
    {
        if (GestureExcludeListBox.SelectedItem is not null)
            GestureExcludeListBox.Items.Remove(GestureExcludeListBox.SelectedItem);
    }

    /// <summary>距離の入力欄を検証する。範囲外なら null。</summary>
    private static int? ParseDistance(string text)
    {
        if (int.TryParse(text.Trim(), out int v)
            && v >= AppSettings.MinGestureDistance && v <= AppSettings.MaxGestureDistance)
            return v;
        return null;
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
        if (TryAddProcess(MinimizeListBox, AppSettings.MaxMinimizeWith, AddProcessBox.Text))
            AddProcessBox.Clear();
    }

    private async void CaptureAddButton_Click(object sender, RoutedEventArgs e) =>
        await CaptureProcessIntoAsync(CaptureAddButton, MinimizeListBox, AppSettings.MaxMinimizeWith);

    /// <summary>数秒待ってから前面ウインドウのプロセス名を取り、リストへ追加する</summary>
    private async Task CaptureProcessIntoAsync(System.Windows.Controls.Button button, System.Windows.Controls.ListBox list, int max)
    {
        object original = button.Content;
        button.IsEnabled = false;
        try
        {
            for (int sec = 3; sec >= 1; sec--)
            {
                button.Content = $"{sec} 秒後...";
                await Task.Delay(1000);
            }

            IntPtr hwnd = NativeMethods.GetForegroundWindow();
            NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
            try
            {
                var proc = Process.GetProcessById((int)pid);
                TryAddProcess(list, max, proc.ProcessName);
            }
            catch
            {
                MessageBox.Show(this, "前面ウインドウのプロセスを取得できませんでした。",
                    "FrontSwitcher", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        finally
        {
            button.Content = original;
            button.IsEnabled = true;
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
    private bool TryAddProcess(System.Windows.Controls.ListBox list, int max, string raw)
    {
        string name = (raw ?? "").Trim();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];
        if (string.IsNullOrWhiteSpace(name))
            return false;

        // 重複（大文字小文字を無視）
        foreach (string item in list.Items)
        {
            if (string.Equals(item, name, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        if (list.Items.Count >= max)
        {
            MessageBox.Show(this, $"登録できるのは最大 {max} 件までです。",
                "FrontSwitcher", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        list.Items.Add(name);
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

        int? startDist = ParseDistance(GestureStartBox.Text);
        int? strokeDist = ParseDistance(GestureStrokeBox.Text);
        if (startDist is null || strokeDist is null)
        {
            MessageBox.Show(this,
                $"マウスジェスチャの判定距離は {AppSettings.MinGestureDistance}〜{AppSettings.MaxGestureDistance} の整数で入力してください。",
                "FrontSwitcher", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        bool gestureOn = GestureEnableCheck.IsChecked == true;
        if (gestureOn && GestureListBox.Items.Count == 0)
        {
            MessageBox.Show(this, "「マウスジェスチャを使う」が ON です。ジェスチャを1件以上登録してください。",
                "FrontSwitcher", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _settings.MouseGestureEnabled = gestureOn;
        _settings.MouseGestures = GestureListBox.Items.Cast<GestureBinding>().Select(g => g.Clone()).ToList();
        _settings.GestureStartDistance = startDist.Value;
        _settings.GestureStrokeDistance = strokeDist.Value;
        _settings.GestureExcludeProcesses = GestureExcludeListBox.Items.Cast<string>().ToList();

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
