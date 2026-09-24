using System.Text;
using System.Text.Json.Serialization;

namespace FrontSwitcher;

/// <summary>マウスジェスチャに割り当てる動作</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GestureAction
{
    /// <summary>前面ウインドウと登録アプリを隠す（トグルの「隠す」側だけ）</summary>
    Hide,
    /// <summary>隠したウインドウを全部戻す</summary>
    Restore,
    /// <summary>ホットキーと同じトグル</summary>
    Switch,
    /// <summary>対象アプリを前面へ（未起動なら起動）</summary>
    BringTarget,
    /// <summary>カーソル下のウインドウを最小化</summary>
    MinimizeWindow,
    /// <summary>カーソル下のウインドウを最大化／元に戻す</summary>
    ToggleMaximizeWindow,
    /// <summary>カーソル下のウインドウを閉じる</summary>
    CloseWindow,
}

/// <summary>
/// ジェスチャ1件の定義（修飾キー＋方向の並び＋動作）。
/// Pattern は U/D/L/R の並び（例: "D", "UR"）。Modifiers は HotKeyService.MOD_* の組み合わせ。
/// </summary>
public sealed class GestureBinding
{
    public uint Modifiers { get; set; }
    public string Pattern { get; set; } = "";
    public GestureAction Action { get; set; }

    /// <summary>1ジェスチャの最大ストローク数</summary>
    public const int MaxStrokes = 4;

    public GestureBinding() { }

    public GestureBinding(uint modifiers, string pattern, GestureAction action)
    {
        Modifiers = modifiers;
        Pattern = pattern;
        Action = action;
    }

    public GestureBinding Clone() => new(Modifiers, Pattern, Action);

    /// <summary>並びが U/D/L/R だけで 1〜4 文字か</summary>
    public static bool IsValidPattern(string? pattern) =>
        !string.IsNullOrEmpty(pattern)
        && pattern.Length <= MaxStrokes
        && pattern.All(c => c is 'U' or 'D' or 'L' or 'R');

    /// <summary>既定の割り当て（↓=隠す、↑=戻す）</summary>
    public static List<GestureBinding> Defaults() => new()
    {
        new(0, "D", GestureAction.Hide),
        new(0, "U", GestureAction.Restore),
    };

    public static string PatternToArrows(string pattern)
    {
        var sb = new StringBuilder();
        foreach (char c in pattern ?? "")
        {
            sb.Append(c switch { 'U' => '↑', 'D' => '↓', 'L' => '←', 'R' => '→', _ => '?' });
        }
        return sb.ToString();
    }

    public static string ModifiersToText(uint modifiers)
    {
        var parts = new List<string>();
        if ((modifiers & HotKeyService.MOD_CONTROL) != 0) parts.Add("Ctrl");
        if ((modifiers & HotKeyService.MOD_SHIFT) != 0) parts.Add("Shift");
        if ((modifiers & HotKeyService.MOD_ALT) != 0) parts.Add("Alt");
        if ((modifiers & HotKeyService.MOD_WIN) != 0) parts.Add("Win");
        return string.Join(" + ", parts);
    }

    public static string ActionToText(GestureAction action) => action switch
    {
        GestureAction.Hide => "隠す",
        GestureAction.Restore => "戻す",
        GestureAction.Switch => "切替（ホットキーと同じ）",
        GestureAction.BringTarget => "対象アプリを前面へ",
        GestureAction.MinimizeWindow => "最小化（カーソル下）",
        GestureAction.ToggleMaximizeWindow => "最大化／元に戻す（カーソル下）",
        GestureAction.CloseWindow => "閉じる（カーソル下）",
        _ => action.ToString(),
    };

    /// <summary>設定画面の一覧表示用（例: "Ctrl + ↓→　：　閉じる"）</summary>
    public override string ToString()
    {
        string mods = ModifiersToText(Modifiers);
        string keys = mods.Length > 0 ? mods + " + " : "";
        return $"{keys}{PatternToArrows(Pattern)}　：　{ActionToText(Action)}";
    }
}
