using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FrontSwitcher;

/// <summary>
/// アプリ設定。%AppData%\FrontSwitcher\settings.json に保存される。
/// </summary>
public sealed class AppSettings
{
    // --- ホットキー ---
    /// <summary>Win32 修飾キー（MOD_ALT=1, MOD_CONTROL=2, MOD_SHIFT=4, MOD_WIN=8 の組み合わせ）</summary>
    public uint Modifiers { get; set; } = HotKeyService.MOD_CONTROL | HotKeyService.MOD_ALT;

    /// <summary>仮想キーコード（VK_*）。既定は Z (0x5A)</summary>
    public uint VirtualKey { get; set; } = 0x5A;

    // --- 対象アプリ ---
    /// <summary>対象ウインドウを探すためのプロセス名（拡張子なし）。例: "notepad"</summary>
    public string TargetProcessName { get; set; } = "";

    /// <summary>対象アプリが起動していないときに実行する exe のフルパス</summary>
    public string TargetExePath { get; set; } = "";

    // --- 動作オプション ---
    /// <summary>
    /// 対象アプリを最前面に出すか。false（既定）ならホットキーで「最小化のみ」行う。
    /// true にすると最小化に加えて対象アプリを前面化（未起動なら起動）する。
    /// </summary>
    public bool BringTargetToFront { get; set; } = false;

    /// <summary>切替時に作業中ウインドウを最小化するか</summary>
    public bool MinimizeCurrent { get; set; } = true;

    /// <summary>最小化時に作業中ウインドウのタスクバーボタンも消すか</summary>
    public bool HideFromTaskbar { get; set; } = false;

    /// <summary>
    /// 一緒に最小化するアプリのプロセス名（拡張子なし）のリスト。最大 20 件。
    /// ここに登録したアプリが開いていれば、ホットキー時に前面ウインドウと一緒に最小化する。
    /// </summary>
    public List<string> MinimizeWithProcesses { get; set; } = new();

    /// <summary>登録可能な「一緒に最小化するアプリ」の上限</summary>
    public const int MaxMinimizeWith = 20;

    /// <summary>
    /// ホットキー時に閉じるブラウザタブのタイトル正規表現リスト。最大 20 件。
    /// Chrome / Edge で、タブのタイトルがいずれかにマッチしたら閉じる。
    /// </summary>
    public List<string> CloseTabPatterns { get; set; } = new();

    /// <summary>登録可能な「閉じるタブの正規表現」の上限</summary>
    public const int MaxCloseTabPatterns = 20;

    // --- マウスジェスチャ ---
    /// <summary>マウスジェスチャ（右ドラッグ）を使うか。既定 OFF。</summary>
    public bool MouseGestureEnabled { get; set; } = false;

    /// <summary>ジェスチャの割り当て一覧。最大 10 件。</summary>
    public List<GestureBinding> MouseGestures { get; set; } = GestureBinding.Defaults();

    /// <summary>登録可能なジェスチャの上限</summary>
    public const int MaxGestures = 10;

    /// <summary>右ボタンを押した地点から何px動いたらジェスチャ開始とするか（物理ピクセル）</summary>
    public int GestureStartDistance { get; set; } = 20;

    /// <summary>別方向へ何px動いたら次のストロークとして数えるか（物理ピクセル）</summary>
    public int GestureStrokeDistance { get; set; } = 30;

    /// <summary>距離設定の下限・上限</summary>
    public const int MinGestureDistance = 5;
    public const int MaxGestureDistance = 300;

    /// <summary>ジェスチャを無効にするアプリのプロセス名（拡張子なし）。最大 20 件。</summary>
    public List<string> GestureExcludeProcesses { get; set; } = new();

    /// <summary>登録可能な除外アプリの上限</summary>
    public const int MaxGestureExclude = 20;

    /// <summary>Windows 起動時に自動起動するか（HKCU Run キー）</summary>
    public bool StartWithWindows { get; set; } = false;

    /// <summary>JSON シリアライズ設定</summary>
    [JsonIgnore]
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    [JsonIgnore]
    public static string SettingsDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FrontSwitcher");

    [JsonIgnore]
    public static string SettingsPath => Path.Combine(SettingsDirectory, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                if (loaded is not null)
                {
                    loaded.Normalize();
                    return loaded;
                }
            }
        }
        catch
        {
            // 壊れた設定ファイルは無視して既定値で続行する
        }
        return new AppSettings();
    }

    /// <summary>手書き等で壊れた値を補正する（null のリスト、範囲外の距離、不正な並び）</summary>
    private void Normalize()
    {
        MinimizeWithProcesses ??= new();
        CloseTabPatterns ??= new();
        GestureExcludeProcesses ??= new();
        MouseGestures ??= new();
        MouseGestures.RemoveAll(g => g is null || !GestureBinding.IsValidPattern(g.Pattern));
        GestureStartDistance = Math.Clamp(GestureStartDistance, MinGestureDistance, MaxGestureDistance);
        GestureStrokeDistance = Math.Clamp(GestureStrokeDistance, MinGestureDistance, MaxGestureDistance);
    }

    public void Save()
    {
        Directory.CreateDirectory(SettingsDirectory);
        var json = JsonSerializer.Serialize(this, JsonOptions);
        File.WriteAllText(SettingsPath, json);
    }

    /// <summary>このインスタンスの複製を返す（設定画面でのキャンセル対応用）。リストは別物として複製する。</summary>
    public AppSettings Clone()
    {
        var copy = (AppSettings)MemberwiseClone();
        copy.MinimizeWithProcesses = new List<string>(MinimizeWithProcesses);
        copy.CloseTabPatterns = new List<string>(CloseTabPatterns);
        copy.MouseGestures = MouseGestures.Select(g => g.Clone()).ToList();
        copy.GestureExcludeProcesses = new List<string>(GestureExcludeProcesses);
        return copy;
    }
}
