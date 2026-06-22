using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace FrontSwitcher;

/// <summary>
/// グローバルホットキーを登録し、押下時にイベントを発火する。
/// メッセージ受信用に不可視の HwndSource を内部生成する。
/// </summary>
public sealed class HotKeyService : IDisposable
{
    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;
    private const uint MOD_NOREPEAT = 0x4000;

    private const int WM_HOTKEY = 0x0312;
    private const int HOTKEY_ID = 0xB001;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private readonly HwndSource _source;
    private bool _registered;

    /// <summary>ホットキーが押されたときに発火</summary>
    public event Action? HotKeyPressed;

    public HotKeyService()
    {
        // 表示されないメッセージ専用ウインドウを作る
        var parameters = new HwndSourceParameters("FrontSwitcherHotKeyWindow")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
            ParentWindow = new IntPtr(-3), // HWND_MESSAGE: メッセージ専用ウインドウ
        };
        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
    }

    /// <summary>設定のホットキーを登録（既存があれば解除してから）。成功で true。</summary>
    public bool Register(uint modifiers, uint virtualKey)
    {
        Unregister();
        if (virtualKey == 0)
            return false;

        _registered = RegisterHotKey(_source.Handle, HOTKEY_ID, modifiers | MOD_NOREPEAT, virtualKey);
        int err = _registered ? 0 : System.Runtime.InteropServices.Marshal.GetLastWin32Error();
        Logger.Log($"RegisterHotKey handle={_source.Handle} mod={modifiers} vk={virtualKey} -> {_registered} (err={err})");
        return _registered;
    }

    public void Unregister()
    {
        if (_registered)
        {
            UnregisterHotKey(_source.Handle, HOTKEY_ID);
            _registered = false;
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HOTKEY_ID)
        {
            Logger.Log("WM_HOTKEY received");
            HotKeyPressed?.Invoke();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        Unregister();
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }
}
