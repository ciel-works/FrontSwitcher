using System.Text;
using System.Windows.Input;

namespace FrontSwitcher;

/// <summary>ホットキーの修飾キー・仮想キーを人間が読める文字列に変換する</summary>
internal static class HotKeyText
{
    public static string Format(uint modifiers, uint virtualKey)
    {
        if (virtualKey == 0)
            return "(未設定)";

        var sb = new StringBuilder();
        if ((modifiers & HotKeyService.MOD_CONTROL) != 0) sb.Append("Ctrl + ");
        if ((modifiers & HotKeyService.MOD_ALT) != 0) sb.Append("Alt + ");
        if ((modifiers & HotKeyService.MOD_SHIFT) != 0) sb.Append("Shift + ");
        if ((modifiers & HotKeyService.MOD_WIN) != 0) sb.Append("Win + ");

        Key key = KeyInterop.KeyFromVirtualKey((int)virtualKey);
        sb.Append(key.ToString());
        return sb.ToString();
    }

    /// <summary>WPF の Key/Modifier から Win32 の (modifiers, vk) を求める</summary>
    public static (uint modifiers, uint vk) FromWpf(Key key, ModifierKeys mods)
    {
        uint m = 0;
        if ((mods & ModifierKeys.Control) != 0) m |= HotKeyService.MOD_CONTROL;
        if ((mods & ModifierKeys.Alt) != 0) m |= HotKeyService.MOD_ALT;
        if ((mods & ModifierKeys.Shift) != 0) m |= HotKeyService.MOD_SHIFT;
        if ((mods & ModifierKeys.Windows) != 0) m |= HotKeyService.MOD_WIN;

        // 呼び出し側で Key.System は実キーに解決済みの想定
        uint vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        return (m, vk);
    }

    /// <summary>修飾キー単体（Ctrl/Alt/Shift/Win）かどうか</summary>
    public static bool IsModifierKey(Key key) => key is
        Key.LeftCtrl or Key.RightCtrl or
        Key.LeftAlt or Key.RightAlt or
        Key.LeftShift or Key.RightShift or
        Key.LWin or Key.RWin or
        Key.System;
}
