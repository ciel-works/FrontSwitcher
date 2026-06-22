using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Automation;

namespace FrontSwitcher;

/// <summary>
/// Chrome / Edge のタブのうち、タイトルが正規表現にマッチするものを閉じる。
/// UI Automation を使う。Chrome は通常ビュー、Edge は Raw ビューでタブ列をたどる
/// （検証で判明した構造の違い）。
/// </summary>
internal static class BrowserTabCloser
{
    // 検証で確認したタブ列のクラス名
    private const string ChromeRegion = "HorizontalTabStripRegionView";
    private const string ChromeCloseButton = "TabCloseButton";
    private const string EdgeRegion = "EdgeTabStripRegionView";
    private const string EdgeStrip = "EdgeTabStrip";
    private const string EdgeContainer = "EdgeTabContainerImpl";
    private const string EdgeTab = "EdgeTab";
    private const string EdgeCloseButton = "EdgeTabCloseButton";

    /// <summary>
    /// マッチするタブを閉じ、閉じた数を返す。UI Automation を確実に動かすため STA スレッドで実行する。
    /// </summary>
    public static int CloseMatchingTabs(IReadOnlyList<string> patterns)
    {
        int result = 0;
        var thread = new Thread(() =>
        {
            try { result = CloseMatchingTabsCore(patterns); }
            catch (Exception ex) { Logger.Log("CloseMatchingTabs error: " + ex.Message); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join();
        return result;
    }

    private static int CloseMatchingTabsCore(IReadOnlyList<string> patterns)
    {
        var regexes = new List<Regex>();
        foreach (string p in patterns)
        {
            if (string.IsNullOrWhiteSpace(p)) continue;
            try { regexes.Add(new Regex(p, RegexOptions.IgnoreCase)); }
            catch (Exception ex) { Logger.Log($"無効な正規表現をスキップ: '{p}' ({ex.Message})"); }
        }
        if (regexes.Count == 0) return 0;

        int closed = 0;
        closed += CloseInChrome(regexes);
        closed += CloseInEdge(regexes);
        if (closed > 0) Logger.Log($"閉じたタブ: {closed}");
        return closed;
    }

    private static bool Matches(List<Regex> regexes, string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        foreach (var rx in regexes)
            if (rx.IsMatch(name)) return true;
        return false;
    }

    // --- Chrome：通常ビューで HorizontalTabStripRegionView 配下の TabItem を探す ---
    private static int CloseInChrome(List<Regex> regexes)
    {
        int closed = 0;
        foreach (IntPtr hwnd in BrowserWindows("chrome"))
        {
            try
            {
                var root = AutomationElement.FromHandle(hwnd);
                var region = root.FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ClassNameProperty, ChromeRegion));
                if (region is null) continue;

                var tabs = region.FindAll(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem));

                // 先に閉じるボタンを集めてから実行（閉じると要素位置がずれるため）
                var buttons = new List<AutomationElement>();
                foreach (AutomationElement tab in tabs)
                {
                    if (!Matches(regexes, SafeName(tab))) continue;
                    var btn = RawChildByClass(tab, ChromeCloseButton);
                    if (btn is not null) buttons.Add(btn);
                }
                foreach (var b in buttons)
                    if (Invoke(b)) closed++;
            }
            catch (Exception ex) { Logger.Log("Chrome tab close error: " + ex.Message); }
        }
        return closed;
    }

    // --- Edge：Raw ビューで Region → Strip → Container → EdgeTab をたどる ---
    private static int CloseInEdge(List<Regex> regexes)
    {
        int closed = 0;
        var walker = TreeWalker.RawViewWalker;
        foreach (IntPtr hwnd in BrowserWindows("msedge"))
        {
            try
            {
                var root = AutomationElement.FromHandle(hwnd);
                var region = root.FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ClassNameProperty, EdgeRegion));
                if (region is null) continue;

                var strip = RawChildByClass(region, EdgeStrip);
                if (strip is null) continue;
                var container = RawChildByClass(strip, EdgeContainer);
                if (container is null) continue;

                var buttons = new List<AutomationElement>();
                AutomationElement? tab = walker.GetFirstChild(container);
                while (tab is not null)
                {
                    if (tab.Current.ClassName == EdgeTab && Matches(regexes, SafeName(tab)))
                    {
                        var btn = RawChildByClass(tab, EdgeCloseButton);
                        if (btn is not null) buttons.Add(btn);
                    }
                    tab = walker.GetNextSibling(tab);
                }
                foreach (var b in buttons)
                    if (Invoke(b)) closed++;
            }
            catch (Exception ex) { Logger.Log("Edge tab close error: " + ex.Message); }
        }
        return closed;
    }

    /// <summary>指定プロセス名の、ウインドウを持つプロセスのウインドウハンドル一覧</summary>
    private static IEnumerable<IntPtr> BrowserWindows(string processName)
    {
        var handles = new List<IntPtr>();
        foreach (var p in Process.GetProcessesByName(processName))
        {
            try
            {
                if (p.MainWindowHandle != IntPtr.Zero)
                    handles.Add(p.MainWindowHandle);
            }
            catch { }
        }
        return handles;
    }

    private static AutomationElement? RawChildByClass(AutomationElement parent, string className)
    {
        var walker = TreeWalker.RawViewWalker;
        AutomationElement? c = walker.GetFirstChild(parent);
        while (c is not null)
        {
            try { if (c.Current.ClassName == className) return c; } catch { }
            c = walker.GetNextSibling(c);
        }
        return null;
    }

    private static string SafeName(AutomationElement el)
    {
        try { return el.Current.Name ?? ""; }
        catch { return ""; }
    }

    private static bool Invoke(AutomationElement button)
    {
        try
        {
            if (button.TryGetCurrentPattern(InvokePattern.Pattern, out object pattern))
            {
                ((InvokePattern)pattern).Invoke();
                return true;
            }
        }
        catch (Exception ex) { Logger.Log("Invoke(close) error: " + ex.Message); }
        return false;
    }
}
