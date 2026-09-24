using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace FrontSwitcher;

/// <summary>
/// 右ドラッグのマウスジェスチャを認識する。
/// 低レベルマウスフック（WH_MOUSE_LL）を専用スレッドで張り、UIスレッドが忙しくても
/// マウス操作が固まらないようにする。フックの中では記録と判定だけ行い、
/// 入力の送り直しや通知はフック終了後に同じスレッドのメッセージループで行う。
/// </summary>
public sealed class MouseGestureService : IDisposable
{
    /// <summary>ジェスチャが成立したとき（フックスレッドから呼ばれる）。引数は定義とカーソル下のウインドウ。</summary>
    public event Action<GestureBinding, IntPtr>? GestureRecognized;

    /// <summary>一時停止中は右ボタンに一切手を出さない</summary>
    public bool Paused { get => _paused; set => _paused = value; }
    private volatile bool _paused;

    public bool IsRunning => _thread is not null;

    // 設定のスナップショット（差し替えは参照の置き換えで行う）
    private sealed class Config
    {
        public required GestureBinding[] Bindings { get; init; }
        public required int StartDistance { get; init; }
        public required int StrokeDistance { get; init; }
        public required HashSet<string> Exclude { get; init; }
    }
    private volatile Config? _config;

    private Thread? _thread;
    private uint _threadId;
    private IntPtr _hook = IntPtr.Zero;
    private readonly LowLevelMouseProc _proc; // GC に回収されないよう保持
    private readonly ConcurrentQueue<Action> _work = new();

    // --- ジェスチャ中の状態（フックスレッドだけが触る） ---
    private enum State { Idle, Pending, Gesturing, Cancelled }
    private State _state = State.Idle;
    private POINT _start;
    private POINT _anchor;
    private uint _mods;
    private IntPtr _target;
    private readonly StringBuilder _pattern = new();
    private bool _overflow;
    private Config? _active;

    // プロセス名のキャッシュ（右ボタン押下のたびに OpenProcess しないため）
    private readonly Dictionary<uint, string> _procNames = new();

    public MouseGestureService()
    {
        _proc = HookProc;
    }

    /// <summary>設定を反映する。ON ならフックを張り、OFF なら外す。</summary>
    public void Apply(AppSettings settings)
    {
        _config = new Config
        {
            Bindings = settings.MouseGestures
                .Where(g => GestureBinding.IsValidPattern(g.Pattern))
                .Select(g => g.Clone())
                .ToArray(),
            StartDistance = Math.Clamp(settings.GestureStartDistance, AppSettings.MinGestureDistance, AppSettings.MaxGestureDistance),
            StrokeDistance = Math.Clamp(settings.GestureStrokeDistance, AppSettings.MinGestureDistance, AppSettings.MaxGestureDistance),
            Exclude = new HashSet<string>(settings.GestureExcludeProcesses, StringComparer.OrdinalIgnoreCase),
        };

        if (settings.MouseGestureEnabled && _config.Bindings.Length > 0)
            Start();
        else
            Stop();
    }

    private void Start()
    {
        if (_thread is not null) return;

        using var ready = new ManualResetEventSlim(false);
        var thread = new Thread(() => HookThreadMain(ready))
        {
            IsBackground = true,
            Name = "FrontSwitcher MouseGesture",
        };
        thread.Start();
        ready.Wait(3000);
        _thread = thread;
    }

    private void Stop()
    {
        var thread = _thread;
        if (thread is null) return;
        PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        thread.Join(2000);
        _thread = null;
    }

    private void HookThreadMain(ManualResetEventSlim ready)
    {
        _threadId = GetCurrentThreadId();
        // PostThreadMessage を受けられるよう、先にメッセージキューを作っておく
        PeekMessage(out _, IntPtr.Zero, 0, 0, PM_NOREMOVE);

        _hook = SetWindowsHookEx(WH_MOUSE_LL, _proc, GetModuleHandle(null), 0);
        int err = _hook == IntPtr.Zero ? Marshal.GetLastWin32Error() : 0;
        Logger.Log($"MouseGesture: フック開始 -> {_hook != IntPtr.Zero} (err={err})");
        ready.Set();
        if (_hook == IntPtr.Zero) return;

        try
        {
            while (GetMessage(out MSG msg, IntPtr.Zero, 0, 0) > 0)
            {
                if (msg.message == WM_APP_WORK)
                {
                    while (_work.TryDequeue(out var job))
                    {
                        try { job(); }
                        catch (Exception ex) { Logger.Log("MouseGesture job error: " + ex.Message); }
                    }
                    continue;
                }
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
        }
        finally
        {
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
            _state = State.Idle;
            Logger.Log("MouseGesture: フック終了");
        }
    }

    /// <summary>フック終了後に実行する処理を積む（入力の送り直し・通知）</summary>
    private void Post(Action job)
    {
        _work.Enqueue(job);
        PostThreadMessage(_threadId, WM_APP_WORK, IntPtr.Zero, IntPtr.Zero);
    }

    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            // 合成入力（自分が送り直したクリックを含む）は素通しする
            if ((data.flags & LLMHF_INJECTED) == 0)
            {
                try
                {
                    if (Handle(wParam.ToInt32(), data))
                        return (IntPtr)1; // 握りつぶす
                }
                catch (Exception ex)
                {
                    _state = State.Idle;
                    Post(() => Logger.Log("MouseGesture hook error: " + ex.Message));
                }
            }
        }
        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    /// <summary>true を返すとそのイベントを握りつぶす</summary>
    private bool Handle(int msg, MSLLHOOKSTRUCT data)
    {
        switch (msg)
        {
            case WM_RBUTTONDOWN:
                // 前回の押下の後始末が漏れていても、新しい押下から始め直す
                _state = State.Idle;
                return BeginIfEligible(data.pt);

            case WM_MOUSEMOVE:
                Track(data.pt);
                return false; // カーソル移動は止めない

            case WM_RBUTTONUP:
                return Finish();

            case WM_LBUTTONDOWN:
            case WM_MBUTTONDOWN:
            case WM_XBUTTONDOWN:
            case WM_MOUSEWHEEL:
            case WM_MOUSEHWHEEL:
                if (_state is State.Pending or State.Gesturing)
                {
                    // 右ボタンを押しながら別の操作 → ジェスチャ中止。
                    // 止めていた右ボタン押下と、今の操作をこの順で送り直す。
                    _state = State.Cancelled;
                    var replay = ToInput(msg, data.mouseData);
                    Post(() =>
                    {
                        var inputs = new List<INPUT> { MouseInput(MOUSEEVENTF_RIGHTDOWN, 0) };
                        if (replay is INPUT r) inputs.Add(r);
                        Send(inputs.ToArray());
                        Logger.Log("MouseGesture: 別のボタン操作で中止");
                    });
                    return true;
                }
                return false;
        }
        return false;
    }

    private bool BeginIfEligible(POINT pt)
    {
        var cfg = _config;
        if (cfg is null || _paused) return false;

        // 左・中ボタンを押しているときは通常操作
        if (IsDown(VK_LBUTTON) || IsDown(VK_MBUTTON)) return false;

        // 押した瞬間の修飾キーに合う登録が無ければ、右ボタンに手を出さない
        uint mods = CurrentModifiers();
        bool any = false;
        foreach (var b in cfg.Bindings)
            if (b.Modifiers == mods) { any = true; break; }
        if (!any) return false;

        IntPtr under = WindowFromPoint(pt);
        IntPtr root = under == IntPtr.Zero ? IntPtr.Zero : GetAncestor(under, GA_ROOT);
        if (cfg.Exclude.Count > 0
            && (IsExcluded(root, cfg) || IsExcluded(GetForegroundWindow(), cfg)))
            return false;

        _state = State.Pending;
        _active = cfg;
        _start = pt;
        _anchor = pt;
        _mods = mods;
        _target = root;
        _pattern.Clear();
        _overflow = false;
        return true;
    }

    private void Track(POINT pt)
    {
        var cfg = _active;
        if (cfg is null) return;

        if (_state == State.Pending)
        {
            int dx = pt.X - _start.X, dy = pt.Y - _start.Y;
            if (Math.Max(Math.Abs(dx), Math.Abs(dy)) >= cfg.StartDistance)
            {
                _state = State.Gesturing;
                AddStroke(DirectionOf(dx, dy));
                _anchor = pt;
            }
        }
        else if (_state == State.Gesturing)
        {
            int dx = pt.X - _anchor.X, dy = pt.Y - _anchor.Y;
            if (dx == 0 && dy == 0) return;
            char dir = DirectionOf(dx, dy);
            char last = _pattern.Length > 0 ? _pattern[^1] : '\0';
            if (dir == last)
            {
                // 同じ向きに進んでいる間は基準点を追いかける（折り返しを折り返し点から測るため）
                _anchor = pt;
            }
            else if (Math.Max(Math.Abs(dx), Math.Abs(dy)) >= cfg.StrokeDistance)
            {
                AddStroke(dir);
                _anchor = pt;
            }
        }
    }

    private void AddStroke(char dir)
    {
        if (_pattern.Length > 0 && _pattern[^1] == dir) return;
        if (_pattern.Length >= GestureBinding.MaxStrokes)
        {
            _overflow = true;
            return;
        }
        _pattern.Append(dir);
    }

    private bool Finish()
    {
        var state = _state;
        _state = State.Idle;
        switch (state)
        {
            case State.Cancelled:
                // 右ボタン押下は送り直し済みなので、離す操作はそのまま通す
                return false;

            case State.Pending:
            {
                // ほとんど動かさずに離した → 普通の右クリックとして送り直す
                uint mods = _mods;
                Post(() =>
                {
                    Send(new[] { MouseInput(MOUSEEVENTF_RIGHTDOWN, 0), MouseInput(MOUSEEVENTF_RIGHTUP, 0) });
                    SendMaskKeyIfNeeded(mods);
                });
                return true;
            }

            case State.Gesturing:
            {
                string pattern = _pattern.ToString();
                bool overflow = _overflow;
                uint mods = _mods;
                IntPtr target = _target;
                var bindings = _active?.Bindings ?? Array.Empty<GestureBinding>();
                Post(() =>
                {
                    SendMaskKeyIfNeeded(mods);
                    string keys = GestureBinding.ModifiersToText(mods);
                    string shown = (keys.Length > 0 ? keys + " + " : "") + GestureBinding.PatternToArrows(pattern);
                    if (overflow)
                    {
                        Logger.Log($"MouseGesture: {shown}… ストローク数オーバーで不成立");
                        return;
                    }
                    var hit = bindings.FirstOrDefault(b => b.Modifiers == mods && b.Pattern == pattern);
                    if (hit is null)
                    {
                        Logger.Log($"MouseGesture: {shown} 未登録");
                        return;
                    }
                    Logger.Log($"MouseGesture: {shown} -> {hit.Action} target={target}");
                    GestureRecognized?.Invoke(hit, target);
                });
                return true;
            }
        }
        return false;
    }

    private static char DirectionOf(int dx, int dy)
    {
        // 画面座標は下が +Y
        if (Math.Abs(dx) > Math.Abs(dy)) return dx > 0 ? 'R' : 'L';
        return dy > 0 ? 'D' : 'U';
    }

    private static uint CurrentModifiers()
    {
        uint m = 0;
        if (IsDown(VK_CONTROL)) m |= HotKeyService.MOD_CONTROL;
        if (IsDown(VK_SHIFT)) m |= HotKeyService.MOD_SHIFT;
        if (IsDown(VK_MENU)) m |= HotKeyService.MOD_ALT;
        if (IsDown(VK_LWIN) || IsDown(VK_RWIN)) m |= HotKeyService.MOD_WIN;
        return m;
    }

    private static bool IsDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    /// <summary>
    /// Alt／Win を押したままジェスチャすると、キーを離したときにメニューバーや
    /// スタートメニューが開いてしまう。未使用キーを1回挟んで「単独押し」でなくする。
    /// </summary>
    private static void SendMaskKeyIfNeeded(uint mods)
    {
        if ((mods & (HotKeyService.MOD_ALT | HotKeyService.MOD_WIN)) == 0) return;
        Send(new[] { KeyInput(VK_MASK, 0), KeyInput(VK_MASK, KEYEVENTF_KEYUP) });
    }

    private bool IsExcluded(IntPtr hwnd, Config cfg)
    {
        if (hwnd == IntPtr.Zero) return false;
        GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0) return false;
        string name = ProcessNameOf(pid);
        return name.Length > 0 && cfg.Exclude.Contains(name);
    }

    private string ProcessNameOf(uint pid)
    {
        if (_procNames.TryGetValue(pid, out var cached)) return cached;
        string name = "";
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h != IntPtr.Zero)
        {
            try
            {
                var sb = new StringBuilder(1024);
                int size = sb.Capacity;
                if (QueryFullProcessImageName(h, 0, sb, ref size))
                    name = Path.GetFileNameWithoutExtension(sb.ToString());
            }
            finally { CloseHandle(h); }
        }
        // PID は使い回されるので、たまったら捨てる
        if (_procNames.Count > 256) _procNames.Clear();
        _procNames[pid] = name;
        return name;
    }

    // --- 入力の送り直し ---
    private static INPUT? ToInput(int msg, uint mouseData)
    {
        uint hi = mouseData >> 16;
        return msg switch
        {
            WM_LBUTTONDOWN => MouseInput(MOUSEEVENTF_LEFTDOWN, 0),
            WM_MBUTTONDOWN => MouseInput(MOUSEEVENTF_MIDDLEDOWN, 0),
            WM_XBUTTONDOWN => MouseInput(MOUSEEVENTF_XDOWN, hi),
            WM_MOUSEWHEEL => MouseInput(MOUSEEVENTF_WHEEL, (uint)(int)(short)hi),
            WM_MOUSEHWHEEL => MouseInput(MOUSEEVENTF_HWHEEL, (uint)(int)(short)hi),
            _ => null,
        };
    }

    private static INPUT MouseInput(uint flags, uint data) => new()
    {
        type = INPUT_MOUSE,
        u = new InputUnion { mi = new MOUSEINPUT { dwFlags = flags, mouseData = data, dwExtraInfo = ExtraInfoMarker } },
    };

    private static INPUT KeyInput(ushort vk, uint flags) => new()
    {
        type = INPUT_KEYBOARD,
        u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = flags, dwExtraInfo = ExtraInfoMarker } },
    };

    private static void Send(INPUT[] inputs)
    {
        uint n = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (n != inputs.Length)
            Logger.Log($"MouseGesture: SendInput {n}/{inputs.Length} (err={Marshal.GetLastWin32Error()})");
    }

    public void Dispose() => Stop();

    // ===== Win32 =====
    private const int WH_MOUSE_LL = 14;
    private const int WM_QUIT = 0x0012;
    private const int WM_APP_WORK = 0x8000 + 0x47; // WM_APP + α
    private const int WM_MOUSEMOVE = 0x0200;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_RBUTTONDOWN = 0x0204;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_MBUTTONDOWN = 0x0207;
    private const int WM_MOUSEWHEEL = 0x020A;
    private const int WM_XBUTTONDOWN = 0x020B;
    private const int WM_MOUSEHWHEEL = 0x020E;
    private const uint LLMHF_INJECTED = 0x01;
    private const uint PM_NOREMOVE = 0;
    private const uint GA_ROOT = 2;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    private const int VK_LBUTTON = 0x01;
    private const int VK_MBUTTON = 0x04;
    private const int VK_SHIFT = 0x10;
    private const int VK_CONTROL = 0x11;
    private const int VK_MENU = 0x12;
    private const int VK_LWIN = 0x5B;
    private const int VK_RWIN = 0x5C;
    private const ushort VK_MASK = 0xE8; // 未割り当ての仮想キー

    private const uint INPUT_MOUSE = 0;
    private const uint INPUT_KEYBOARD = 1;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    private const uint MOUSEEVENTF_XDOWN = 0x0080;
    private const uint MOUSEEVENTF_WHEEL = 0x0800;
    private const uint MOUSEEVENTF_HWHEEL = 0x1000;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private static readonly IntPtr ExtraInfoMarker = new(0x46535747); // "FSWG"

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public int message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
        public uint lPrivate;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion u;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint idThread, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(IntPtr hProcess, uint dwFlags, StringBuilder lpExeName, ref int lpdwSize);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);
}
