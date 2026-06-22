using System.Runtime.InteropServices;

namespace FrontSwitcher;

/// <summary>
/// タスクバーのボタン表示を制御する。ウインドウ自体は隠さず、タスクバーの
/// ボタンだけを消す/戻す（Alt+Tab には残る）。Windows の ITaskbarList(COM) を使う。
/// </summary>
internal static class TaskbarButton
{
    // ITaskbarList インターフェース
    [ComImport]
    [Guid("56FDF342-FD6D-11d0-958A-006097C9A090")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList
    {
        void HrInit();
        void AddTab(IntPtr hwnd);
        void DeleteTab(IntPtr hwnd);
        void ActivateTab(IntPtr hwnd);
        void SetActiveAlt(IntPtr hwnd);
    }

    // TaskbarList コクラス
    [ComImport]
    [Guid("56FDF344-FD6D-11d0-958A-006097C9A090")]
    [ClassInterface(ClassInterfaceType.None)]
    private class TaskbarListClass { }

    private static ITaskbarList? _instance;

    private static ITaskbarList? Instance
    {
        get
        {
            if (_instance is null)
            {
                try
                {
                    var obj = (ITaskbarList)new TaskbarListClass();
                    obj.HrInit();
                    _instance = obj;
                }
                catch (Exception ex)
                {
                    Logger.Log($"ITaskbarList 初期化失敗: {ex.Message}");
                }
            }
            return _instance;
        }
    }

    /// <summary>タスクバーのボタンを消す</summary>
    public static void Hide(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return;
        try { Instance?.DeleteTab(hWnd); }
        catch (Exception ex) { Logger.Log($"DeleteTab 失敗: {ex.Message}"); }
    }

    /// <summary>タスクバーのボタンを戻す</summary>
    public static void Show(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return;
        try { Instance?.AddTab(hWnd); }
        catch (Exception ex) { Logger.Log($"AddTab 失敗: {ex.Message}"); }
    }
}
