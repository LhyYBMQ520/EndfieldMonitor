using EndfieldMonitor.Interop;
using static EndfieldMonitor.Interop.NativeMethods;

namespace EndfieldMonitor.Services;

/// <summary>物理像素矩形（虚拟屏幕坐标，可为负）。</summary>
public readonly record struct PxRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;

    public long IntersectionArea(PxRect o)
    {
        long w = Math.Min(Right, o.Right) - Math.Max(X, o.X);
        long h = Math.Min(Bottom, o.Bottom) - Math.Max(Y, o.Y);
        return w > 0 && h > 0 ? w * h : 0;
    }

    public double DistanceSquaredTo(PxRect o)
    {
        double dx = Math.Max(0, Math.Max(o.X - Right, X - o.Right));
        double dy = Math.Max(0, Math.Max(o.Y - Bottom, Y - o.Bottom));
        return dx * dx + dy * dy;
    }
}

public readonly record struct MonitorArea(PxRect Bounds, PxRect Work, uint Dpi, bool Primary);

public readonly record struct PlacementResult(PxRect Rect, int MonitorIndex, double Shrink);

/// <summary>
/// 窗口位置恢复与屏幕可达性校验。核心算法为纯函数（<see cref="Place"/>），
/// 便于针对负坐标、多显示器、超小工作区进行测试。
/// </summary>
public static class WindowPlacement
{
    /// <summary>默认位置距工作区右上角的边距（DIP）。</summary>
    public const double DefaultMarginDip = 32;

    /// <summary>
    /// 校验并修正窗口矩形：
    /// 1. 选择与窗口交集最大的工作区；完全不相交时选距离最近者；
    /// 2. 若窗口大于该工作区，计算收缩比例（不修改用户保存的缩放）；
    /// 3. 将窗口完整夹入工作区，保证仪表主体与右上角关闭入口可达。
    /// </summary>
    /// <param name="desired">期望矩形（像素），尺寸按目标显示器 DPI 计算前的近似值。</param>
    /// <param name="monitors">当前显示器。</param>
    /// <param name="sizeForDpi">给定 DPI 下的窗口像素尺寸（缩放 100% 的收缩前）。</param>
    public static PlacementResult Place(PxRect desired, IReadOnlyList<MonitorArea> monitors, Func<uint, (int W, int H)> sizeForDpi)
    {
        if (monitors.Count == 0)
            return new PlacementResult(desired, -1, 1);

        int best = -1;
        long bestArea = 0;
        for (int i = 0; i < monitors.Count; i++)
        {
            long a = desired.IntersectionArea(monitors[i].Work);
            if (a > bestArea) { bestArea = a; best = i; }
        }
        if (best < 0)
        {
            double bestD = double.MaxValue;
            for (int i = 0; i < monitors.Count; i++)
            {
                double d = desired.DistanceSquaredTo(monitors[i].Work);
                if (d < bestD) { bestD = d; best = i; }
            }
        }

        var m = monitors[best];
        var (w, h) = sizeForDpi(m.Dpi);
        double shrink = 1;
        if (w > m.Work.Width || h > m.Work.Height)
        {
            shrink = Math.Min((double)m.Work.Width / w, (double)m.Work.Height / h);
            shrink = Math.Max(0.1, shrink);
            w = (int)Math.Floor(w * shrink);
            h = (int)Math.Floor(h * shrink);
        }

        int x = Math.Clamp(desired.X, m.Work.X, m.Work.Right - w);
        int y = Math.Clamp(desired.Y, m.Work.Y, m.Work.Bottom - h);
        return new PlacementResult(new PxRect(x, y, w, h), best, shrink);
    }

    /// <summary>主显示器工作区右上角的默认位置。</summary>
    public static PxRect DefaultRect(IReadOnlyList<MonitorArea> monitors, Func<uint, (int W, int H)> sizeForDpi)
    {
        var m = monitors.FirstOrDefault(x => x.Primary);
        if (m.Work.Width == 0 && monitors.Count > 0) m = monitors[0];
        var (w, h) = sizeForDpi(m.Dpi == 0 ? 96 : m.Dpi);
        int margin = (int)Math.Round(DefaultMarginDip * (m.Dpi == 0 ? 96 : m.Dpi) / 96.0);
        return new PxRect(m.Work.Right - w - margin, m.Work.Y + margin, w, h);
    }

    /// <summary>枚举当前显示器（物理像素 + 有效 DPI）。</summary>
    public static List<MonitorArea> EnumerateMonitors()
    {
        var list = new List<MonitorArea>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMon, IntPtr _, ref RECT _, IntPtr _) =>
        {
            var info = new MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfo(hMon, ref info))
            {
                uint dpi = 96;
                try { if (GetDpiForMonitor(hMon, MDT_EFFECTIVE_DPI, out var dx, out _) == 0) dpi = dx; } catch { }
                list.Add(new MonitorArea(ToPx(info.rcMonitor), ToPx(info.rcWork), dpi, (info.dwFlags & 1) != 0));
            }
            return true;
        }, IntPtr.Zero);
        return list;
    }

    private static PxRect ToPx(RECT r) => new(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
}
