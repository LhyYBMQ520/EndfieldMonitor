namespace EndfieldMonitor.Rendering;

/// <summary>
/// 粒子层软件光栅化：在预乘 BGRA 缓冲区中按深度层合成。
/// 所有粒子颜色相同 (98,98,98)，因此只需维护每像素 alpha；
/// 同一深度层内的覆盖合并后再以层透明度合成（与参考 canvas 中“每层一个 path、一次 fill”一致）。
/// 缓冲区复用，每帧不分配。
/// </summary>
public sealed class ParticleRasterizer
{
    public const double CanvasSize = 506;      // 设计单位
    public const double CanvasCenter = 253;
    public const double ClipRadius = 246;
    private const byte Gray = 98;

    private float[] _alpha = Array.Empty<float>();
    private float[] _before = Array.Empty<float>();
    private float[] _cover = Array.Empty<float>();
    private int[] _stamp = Array.Empty<int>();
    private int[] _order = new int[ParticleModel.ParticleCount];
    private readonly int[] _layerStart = new int[ParticleModel.LayerCount + 1];
    private int _currentStamp;

    public int PixelSize { get; private set; }
    public uint[] Pixels { get; private set; } = Array.Empty<uint>();

    public void EnsureSize(int pixelSize)
    {
        pixelSize = Math.Clamp(pixelSize, 16, 2048);
        if (pixelSize == PixelSize) return;
        PixelSize = pixelSize;
        int n = pixelSize * pixelSize;
        _alpha = new float[n];
        _before = new float[n];
        _cover = new float[n];
        _stamp = new int[n];
        Pixels = new uint[n];
        _currentStamp = 0;
    }

    /// <summary>把模型当前帧绘制到 <see cref="Pixels"/>。</summary>
    public void Render(ParticleModel model)
    {
        int size = PixelSize;
        if (size == 0) return;
        Array.Clear(_alpha);

        double scale = size / CanvasSize;       // 像素 / 设计单位
        double fit = model.Fit;
        double centerPx = CanvasCenter * scale;
        double clipPx = ClipRadius * scale;
        double clip2 = clipPx * clipPx;

        // 按深度层计数排序（稳定，复用数组）
        var layers = model.Layer;
        Array.Clear(_layerStart);
        for (int i = 0; i < layers.Length; i++) _layerStart[layers[i] + 1]++;
        for (int l = 0; l < ParticleModel.LayerCount; l++) _layerStart[l + 1] += _layerStart[l];
        Span<int> cursor = stackalloc int[ParticleModel.LayerCount];
        for (int l = 0; l < ParticleModel.LayerCount; l++) cursor[l] = _layerStart[l];
        for (int i = 0; i < layers.Length; i++) _order[cursor[layers[i]]++] = i;

        for (int layer = 0; layer < ParticleModel.LayerCount; layer++)
        {
            if (++_currentStamp == int.MaxValue) { Array.Clear(_stamp); _currentStamp = 1; }
            int stamp = _currentStamp;
            float layerAlpha = (float)ParticleModel.LayerAlpha(layer);
            double sizeUnits = ParticleModel.LayerSize(layer);
            double rx = sizeUnits * 0.77 * fit * scale;
            double ry = sizeUnits * 1.25 * fit * scale;

            for (int k = _layerStart[layer]; k < _layerStart[layer + 1]; k++)
            {
                int p = _order[k];
                int row = ParticleModel.RowOf(p);
                double cx = centerPx + model.X[p] * fit * scale;
                double cy = centerPx + model.Y[p] * fit * scale;
                double dxc = cx - centerPx, dyc = cy - centerPx;
                if (dxc * dxc + dyc * dyc > clip2) continue;

                if (Math.Min(rx, ry) < 0.75)
                    SplatSmall(cx, cy, Math.PI * rx * ry, stamp, layerAlpha);
                else
                    SplatEllipse(cx, cy, rx, ry, model.RowCosAngle[row], model.RowSinAngle[row], stamp, layerAlpha);
            }
        }

        var alpha = _alpha;
        var pixels = Pixels;
        for (int i = 0; i < pixels.Length; i++)
        {
            float a = alpha[i];
            if (a <= 0.0015f) { pixels[i] = 0; continue; }
            if (a > 1f) a = 1f;
            uint ab = (uint)(a * 255f + 0.5f);
            uint g = (uint)(a * Gray + 0.5f);
            pixels[i] = (ab << 24) | (g << 16) | (g << 8) | g;
        }
    }

    private void Accumulate(int idx, float coverage, int stamp, float layerAlpha)
    {
        if (_stamp[idx] != stamp)
        {
            _stamp[idx] = stamp;
            _before[idx] = _alpha[idx];
            _cover[idx] = 0;
        }
        float c = _cover[idx] + coverage;
        if (c > 1f) c = 1f;
        _cover[idx] = c;
        float b = _before[idx];
        _alpha[idx] = b + layerAlpha * c * (1f - b);
    }

    /// <summary>亚像素粒子：按面积做双线性分配，保持总覆盖量。</summary>
    private void SplatSmall(double cx, double cy, double area, int stamp, float layerAlpha)
    {
        double fx = cx - 0.5, fy = cy - 0.5;
        int x0 = (int)Math.Floor(fx), y0 = (int)Math.Floor(fy);
        float tx = (float)(fx - x0), ty = (float)(fy - y0);
        float a = (float)Math.Min(area, 1.0);
        int size = PixelSize;
        if (x0 < 0 || y0 < 0 || x0 + 1 >= size || y0 + 1 >= size) return;
        int i = y0 * size + x0;
        Accumulate(i, a * (1 - tx) * (1 - ty), stamp, layerAlpha);
        Accumulate(i + 1, a * tx * (1 - ty), stamp, layerAlpha);
        Accumulate(i + size, a * (1 - tx) * ty, stamp, layerAlpha);
        Accumulate(i + size + 1, a * tx * ty, stamp, layerAlpha);
    }

    /// <summary>较大粒子：旋转椭圆，1 像素宽的抗锯齿边缘。</summary>
    private void SplatEllipse(double cx, double cy, double rx, double ry, double cos, double sin, int stamp, float layerAlpha)
    {
        int size = PixelSize;
        double ext = Math.Max(rx, ry) + 1;
        int xMin = Math.Max(0, (int)Math.Floor(cx - ext)), xMax = Math.Min(size - 1, (int)Math.Ceiling(cx + ext));
        int yMin = Math.Max(0, (int)Math.Floor(cy - ext)), yMax = Math.Min(size - 1, (int)Math.Ceiling(cy + ext));
        double rEff = Math.Sqrt(rx * ry);
        double irx = 1 / rx, iry = 1 / ry;
        for (int y = yMin; y <= yMax; y++)
        {
            double dy = y + 0.5 - cy;
            int rowBase = y * size;
            for (int x = xMin; x <= xMax; x++)
            {
                double dx = x + 0.5 - cx;
                double u = (dx * cos + dy * sin) * irx;
                double v = (-dx * sin + dy * cos) * iry;
                double sd = (Math.Sqrt(u * u + v * v) - 1) * rEff;
                double c = 0.5 - sd;
                if (c <= 0) continue;
                Accumulate(rowBase + x, (float)(c > 1 ? 1 : c), stamp, layerAlpha);
            }
        }
    }
}
