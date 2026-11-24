using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using EndfieldMonitor.Interop;
using static EndfieldMonitor.Interop.NativeMethods;

namespace EndfieldMonitor.Services;

public enum TrayCommand
{
    ToggleVisible = 1,
    ToggleTopmost,
    ToggleLock,
    OpenSettings,
    ResetPosition,
    Exit,
    ShowWindow,
    Scale60 = 100, Scale80, Scale100, Scale120, Scale140, Scale160,
}

/// <summary>托盘菜单需要显示的状态（由统一的应用状态提供，托盘自身不保存副本）。</summary>
public readonly record struct TrayMenuState(bool WidgetVisible, bool Topmost, bool Locked, double Scale);

/// <summary>
/// 原生 Shell_NotifyIcon 托盘。使用独立的隐藏顶层窗口接收回调，因此主窗口的锁定 / 穿透
/// 样式不会影响托盘；右键菜单为原生 Win32 弹出菜单。处理 Explorer 重启（TaskbarCreated）。
/// 所有回调都在创建它的 WPF UI 线程上执行。
/// </summary>
public sealed class TrayService : IDisposable
{
    private const uint IconId = 1;
    private const int CallbackMessage = WM_APP + 1;

    private readonly Func<TrayMenuState> _stateProvider;
    private readonly HwndSource _source;
    private readonly uint _taskbarCreatedMsg;
    private readonly DispatcherTimer _retryTimer;
    private IntPtr _icon;
    private string _tooltip = "EndfieldMonitor";
    private bool _disposed;

    public TrayService(Func<TrayMenuState> stateProvider)
    {
        _stateProvider = stateProvider;
        var p = new HwndSourceParameters("EndfieldMonitor.TrayHost")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0, // WS_OVERLAPPED，不可见；必须是顶层窗口才能收到 TaskbarCreated 广播
            ExtendedWindowStyle = (int)WS_EX_TOOLWINDOW,
        };
        _source = new HwndSource(p);
        _source.AddHook(WndProc);
        _taskbarCreatedMsg = RegisterWindowMessage("TaskbarCreated");
        // 管理员运行时允许 Explorer（中等完整性）发送这些消息。
        ChangeWindowMessageFilterEx(_source.Handle, _taskbarCreatedMsg, MSGFLT_ALLOW, IntPtr.Zero);
        ChangeWindowMessageFilterEx(_source.Handle, CallbackMessage, MSGFLT_ALLOW, IntPtr.Zero);
        _icon = LoadAppIcon(GetSystemMetricsForDpi(SM_CXSMICON, GetDpiForSystem()));
        _retryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _retryTimer.Tick += (_, _) => { if (TryAdd()) _retryTimer.Stop(); };
    }

    public event Action<TrayCommand>? Command;
    /// <summary>托盘图标成功建立（含 Explorer 重启后恢复）。</summary>
    public event Action? Established;

    public bool IsEstablished { get; private set; }
    public IntPtr Handle => _source.Handle;

    /// <summary>建立托盘图标；失败时自动定时重试。</summary>
    public bool Initialize()
    {
        if (TryAdd()) return true;
        _retryTimer.Start();
        return false;
    }

    public void SetTooltip(string text)
    {
        _tooltip = text.Length > 127 ? text[..127] : text;
        if (!IsEstablished) return;
        var d = CreateData(NIF_TIP | NIF_SHOWTIP);
        Shell_NotifyIcon(NIM_MODIFY, ref d);
    }

    private NOTIFYICONDATAW CreateData(uint flags) => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATAW>(),
        hWnd = _source.Handle,
        uID = IconId,
        uFlags = flags,
        uCallbackMessage = CallbackMessage,
        hIcon = _icon,
        szTip = _tooltip,
        szInfo = string.Empty,
        szInfoTitle = string.Empty,
    };

    private bool TryAdd()
    {
        if (_disposed) return false;
        var d = CreateData(NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP);
        // 先删除可能残留的旧图标（Explorer 重启时旧图标已不存在，删除失败无害）。
        Shell_NotifyIcon(NIM_DELETE, ref d);
        bool ok = Shell_NotifyIcon(NIM_ADD, ref d);
        if (ok)
        {
            d.uVersion = NOTIFYICON_VERSION_4;
            Shell_NotifyIcon(NIM_SETVERSION, ref d);
        }
        bool wasEstablished = IsEstablished;
        IsEstablished = ok;
        if (ok && !wasEstablished) Established?.Invoke();
        return ok;
    }

    /// <summary>Explorer / taskkill 发送给隐藏宿主窗口的关闭请求（WM_CLOSE / WM_QUERYENDSESSION）。</summary>
    public event Action? CloseRequested;

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == 0x0010 /* WM_CLOSE */)
        {
            // taskkill（不带 /F）会向进程的所有顶层窗口发送 WM_CLOSE；把它转为应用退出，
            // 而不是只销毁托盘宿主窗口留下无托盘的残留进程。
            handled = true;
            CloseRequested?.Invoke();
            return IntPtr.Zero;
        }
        if (msg == (int)_taskbarCreatedMsg && _taskbarCreatedMsg != 0)
        {
            IsEstablished = false;
            if (!TryAdd()) _retryTimer.Start();
            return IntPtr.Zero;
        }
        if (msg == CallbackMessage)
        {
            // NOTIFYICON_VERSION_4：LOWORD(lParam) 为事件，wParam 为锚点坐标。
            int ev = LoWord(lParam);
            switch (ev)
            {
                case WM_LBUTTONDBLCLK:
                    Command?.Invoke(TrayCommand.ShowWindow);
                    break;
                case WM_CONTEXTMENU:
                case WM_RBUTTONUP:
                    ShowMenu(LoWord(wParam), HiWord(wParam));
                    break;
            }
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void ShowMenu(int x, int y)
    {
        var s = _stateProvider();
        IntPtr menu = CreatePopupMenu();
        IntPtr scaleMenu = CreatePopupMenu();
        try
        {
            AppendMenu(menu, MF_STRING, (UIntPtr)(uint)TrayCommand.ToggleVisible, s.WidgetVisible ? "隐藏挂件" : "显示挂件");
            AppendMenu(menu, MF_SEPARATOR, UIntPtr.Zero, null);
            AppendMenu(menu, MF_STRING | (s.Topmost ? MF_CHECKED : 0), (UIntPtr)(uint)TrayCommand.ToggleTopmost, "窗口置顶");
            AppendMenu(menu, MF_STRING | (s.Locked ? MF_CHECKED : 0), (UIntPtr)(uint)TrayCommand.ToggleLock, "锁定挂件（鼠标穿透）");
            AppendMenu(menu, MF_SEPARATOR, UIntPtr.Zero, null);
            AppendMenu(menu, MF_STRING, (UIntPtr)(uint)TrayCommand.OpenSettings, "打开监控设置");
            int i = 0;
            foreach (var step in Models.AppSettings.ScaleSteps)
            {
                bool check = Math.Abs(step - s.Scale) < 0.001;
                AppendMenu(scaleMenu, MF_STRING | (check ? MF_CHECKED : 0), (UIntPtr)(uint)(TrayCommand.Scale60 + i), $"{step * 100:0}%");
                i++;
            }
            AppendMenu(menu, MF_POPUP, (UIntPtr)(ulong)scaleMenu, "缩放");
            AppendMenu(menu, MF_STRING, (UIntPtr)(uint)TrayCommand.ResetPosition, "恢复默认位置");
            AppendMenu(menu, MF_SEPARATOR, UIntPtr.Zero, null);
            AppendMenu(menu, MF_STRING, (UIntPtr)(uint)TrayCommand.Exit, "退出");

            if (x == 0 && y == 0 && GetCursorPos(out var pt)) { x = pt.X; y = pt.Y; }
            // 必须让托盘宿主成为前台窗口，菜单才会在外部点击时关闭（Shell 文档要求）。
            SetForegroundWindow(_source.Handle);
            int cmd = TrackPopupMenuEx(menu, TPM_RIGHTBUTTON | TPM_BOTTOMALIGN | TPM_RETURNCMD | TPM_NONOTIFY, x, y, _source.Handle, IntPtr.Zero);
            PostMessage(_source.Handle, WM_NULL, IntPtr.Zero, IntPtr.Zero);
            if (cmd != 0) Command?.Invoke((TrayCommand)cmd);
        }
        finally
        {
            DestroyMenu(menu); // 同时销毁子菜单
        }
    }

    /// <summary>从嵌入的 ICO 中选择最接近目标尺寸的图像生成 HICON。</summary>
    private static IntPtr LoadAppIcon(int size)
    {
        try
        {
            using var stream = typeof(TrayService).Assembly.GetManifestResourceStream("EndfieldMonitor.app.ico");
            if (stream == null) return IntPtr.Zero;
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            byte[] ico = ms.ToArray();
            int count = BitConverter.ToUInt16(ico, 4);
            int bestIndex = -1, bestDelta = int.MaxValue;
            for (int i = 0; i < count; i++)
            {
                int off = 6 + i * 16;
                int w = ico[off] == 0 ? 256 : ico[off];
                int delta = w >= size ? w - size : (size - w) * 4; // 优先略大的图像再缩小
                if (delta < bestDelta) { bestDelta = delta; bestIndex = i; }
            }
            if (bestIndex < 0) return IntPtr.Zero;
            int entry = 6 + bestIndex * 16;
            int len = BitConverter.ToInt32(ico, entry + 8);
            int dataOff = BitConverter.ToInt32(ico, entry + 12);
            var bits = new byte[len];
            Buffer.BlockCopy(ico, dataOff, bits, 0, len);
            return CreateIconFromResourceEx(bits, (uint)len, true, 0x00030000, size, size, 0);
        }
        catch { return IntPtr.Zero; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _retryTimer.Stop();
        var d = CreateData(0);
        Shell_NotifyIcon(NIM_DELETE, ref d);
        IsEstablished = false;
        _source.RemoveHook(WndProc);
        _source.Dispose();
        if (_icon != IntPtr.Zero) { DestroyIcon(_icon); _icon = IntPtr.Zero; }
        Command = null;
        Established = null;
        CloseRequested = null;
    }
}
