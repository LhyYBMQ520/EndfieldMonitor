using System.Windows.Interop;
using EndfieldMonitor.Interop;
using static EndfieldMonitor.Interop.NativeMethods;

namespace EndfieldMonitor.Services;

/// <summary>实例间仅允许的命令集合（白名单整数码，不传递任何字符串或可执行内容）。</summary>
public enum InstanceCommand { Show = 1, Unlock = 2, ResetWindow = 3, OpenSettings = 4, Hide = 5, Exit = 6 }

/// <summary>
/// 单实例：会话内命名 Mutex + 隐藏顶层窗口接收已注册的窗口消息。
/// 选择窗口消息而非命名管道，是因为管理员实例创建的管道会带高完整性标签，普通权限启动的
/// 救援命令无法连接；窗口消息可用 ChangeWindowMessageFilterEx 只放行这一个消息。
/// 消息 wParam 只接受 <see cref="InstanceCommand"/> 中定义的值。
/// </summary>
public sealed class SingleInstanceService : IDisposable
{
    private const string WindowTitle = "EndfieldMonitor.InstanceHost";
    private const string MessageName = "EndfieldMonitor.InstanceCommand.v1";
    private const string MutexName = @"Local\EndfieldMonitor.SingleInstance";

    private readonly uint _message;
    private Mutex? _mutex;
    private HwndSource? _host;

    public SingleInstanceService()
    {
        _message = RegisterWindowMessage(MessageName);
    }

    /// <summary>在 UI 线程上触发。</summary>
    public event Action<InstanceCommand>? CommandReceived;

    /// <summary>尝试成为主实例；可选等待指定进程退出（管理员重启时等待旧实例释放）。</summary>
    public bool TryAcquire(int? waitForPid = null)
    {
        if (waitForPid is int pid)
        {
            try
            {
                using var p = System.Diagnostics.Process.GetProcessById(pid);
                p.WaitForExit(15000);
            }
            catch { /* 进程已退出 */ }
        }

        for (int attempt = 0; attempt < (waitForPid.HasValue ? 30 : 1); attempt++)
        {
            try
            {
                _mutex = new Mutex(true, MutexName, out bool created);
                if (created) return true;
                try
                {
                    if (_mutex.WaitOne(0)) return true;
                }
                catch (AbandonedMutexException) { return true; }
                _mutex.Dispose();
                _mutex = null;
            }
            catch (UnauthorizedAccessException)
            {
                // 已有以管理员身份运行的实例，当前普通权限进程无权打开其 Mutex。
                _mutex = null;
            }
            if (waitForPid.HasValue) Thread.Sleep(200);
        }
        return false;
    }

    public static IReadOnlyList<InstanceCommand> ParseArgs(IEnumerable<string> args)
    {
        var list = new List<InstanceCommand>();
        foreach (var raw in args)
        {
            switch (raw.Trim().ToLowerInvariant())
            {
                case "--unlock": list.Add(InstanceCommand.Unlock); break;
                case "--reset-window": list.Add(InstanceCommand.ResetWindow); break;
                case "--settings": list.Add(InstanceCommand.OpenSettings); break;
                case "--hide": list.Add(InstanceCommand.Hide); break;
                case "--exit": list.Add(InstanceCommand.Exit); break;
            }
        }
        if (list.Count == 0) list.Add(InstanceCommand.Show);
        return list;
    }

    public static int? ParseWaitPid(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "--wait-pid" && int.TryParse(args[i + 1], out int pid) && pid > 0) return pid;
        return null;
    }

    /// <summary>作为第二实例：把命令发给主实例。</summary>
    public bool SendToPrimary(IEnumerable<InstanceCommand> commands)
    {
        IntPtr target = IntPtr.Zero;
        for (int i = 0; i < 20 && target == IntPtr.Zero; i++)
        {
            target = FindWindow(null, WindowTitle);
            if (target == IntPtr.Zero) Thread.Sleep(100);
        }
        if (target == IntPtr.Zero || _message == 0) return false;
        // 允许主实例把挂件带到前台（仅在显示时使用）。
        AllowSetForegroundWindow(ASFW_ANY);
        bool ok = true;
        foreach (var c in commands) ok &= PostMessage(target, (int)_message, (IntPtr)(int)c, IntPtr.Zero);
        return ok;
    }

    /// <summary>主实例：创建接收窗口（必须在 UI 线程调用）。</summary>
    public void StartListening()
    {
        var p = new HwndSourceParameters(WindowTitle) { Width = 0, Height = 0, WindowStyle = 0, ExtendedWindowStyle = (int)WS_EX_TOOLWINDOW };
        _host = new HwndSource(p);
        _host.AddHook(WndProc);
        if (_message != 0) ChangeWindowMessageFilterEx(_host.Handle, _message, MSGFLT_ALLOW, IntPtr.Zero);
    }

    /// <summary>外部关闭请求（taskkill 发送的 WM_CLOSE）。</summary>
    public event Action? CloseRequested;

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == 0x0010 /* WM_CLOSE */)
        {
            handled = true;
            CloseRequested?.Invoke();
            return IntPtr.Zero;
        }
        if (_message != 0 && msg == (int)_message)
        {
            handled = true;
            long code = wParam.ToInt64();
            if (code is >= 1 and <= 6 && Enum.IsDefined(typeof(InstanceCommand), (int)code))
                CommandReceived?.Invoke((InstanceCommand)(int)code);
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_host != null)
        {
            _host.RemoveHook(WndProc);
            _host.Dispose();
            _host = null;
        }
        if (_mutex != null)
        {
            try { _mutex.ReleaseMutex(); } catch { }
            _mutex.Dispose();
            _mutex = null;
        }
        CommandReceived = null;
        CloseRequested = null;
    }
}
