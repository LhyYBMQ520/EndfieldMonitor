namespace EndfieldMonitor.Services;

/// <summary>
/// 挂件状态的唯一来源（可见、置顶、锁定）及其切换规则。纯逻辑，不依赖 WPF，便于测试。
/// 托盘、设置面板、窗口都只通过它读写状态。
/// </summary>
public sealed class WidgetState
{
    public bool Visible { get; private set; } = true;
    public bool Topmost { get; private set; }
    public bool Locked { get; private set; }
    public bool TrayReady { get; private set; }

    /// <summary>启动时从配置恢复、但因托盘尚未建立而暂缓的锁定。</summary>
    public bool PendingLock { get; private set; }

    public event Action? Changed;

    public void Restore(bool visible, bool topmost, bool locked)
    {
        Visible = visible;
        Topmost = topmost;
        Locked = false;
        PendingLock = locked;
        ApplyPending();
        Changed?.Invoke();
    }

    public void SetTrayReady(bool ready)
    {
        TrayReady = ready;
        if (!ready && Locked)
        {
            // 托盘丢失（例如 Explorer 重启尚未恢复）时保持锁定：托盘恢复后仍可解锁；
            // 若长时间无法恢复，--unlock 救援命令可用。
        }
        ApplyPending();
        Changed?.Invoke();
    }

    private void ApplyPending()
    {
        if (PendingLock && TrayReady)
        {
            Locked = true;
            PendingLock = false;
        }
    }

    /// <summary>请求锁定 / 解锁。没有可用托盘时拒绝锁定，避免进入无法操作的状态。</summary>
    public bool SetLocked(bool locked)
    {
        if (locked && !TrayReady) return false;
        PendingLock = false;
        if (Locked == locked) return true;
        Locked = locked;
        Changed?.Invoke();
        return true;
    }

    public void SetTopmost(bool topmost)
    {
        if (Topmost == topmost) return;
        Topmost = topmost;
        Changed?.Invoke();
    }

    /// <summary>显示 / 隐藏，不改变锁定状态。</summary>
    public void SetVisible(bool visible)
    {
        if (Visible == visible) return;
        Visible = visible;
        Changed?.Invoke();
    }

    /// <summary>“打开监控设置”：显示挂件并解除锁定。</summary>
    public void PrepareForSettings()
    {
        bool changed = !Visible || Locked || PendingLock;
        Visible = true;
        Locked = false;
        PendingLock = false;
        if (changed) Changed?.Invoke();
    }

    /// <summary>--unlock 救援。</summary>
    public void RescueUnlock()
    {
        Visible = true;
        Locked = false;
        PendingLock = false;
        Changed?.Invoke();
    }
}
