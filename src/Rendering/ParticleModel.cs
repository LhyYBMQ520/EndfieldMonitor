namespace EndfieldMonitor.Rendering;

/// <summary>
/// 参考页粒子环面的 C# 移植：168×68 个拓扑固定的粒子，9 个连续随机漂移通道，
/// 每帧仅重新计算位置，不分配新对象。坐标以 506×506 粒子画布中心为原点（设计单位）。
/// </summary>
public sealed class ParticleModel
{
    public const int RowCount = 168;
    public const int ColumnCount = 68;
    public const int ParticleCount = RowCount * ColumnCount;
    public const int LayerCount = 16;
    public const double PlaybackRate = 2;         // 参考 particlePlaybackRate = 2
    public const double FitRadius = 242;          // 参考 fit = min(1, 242 / maxRadius)

    // 行数据（每行共享）
    private readonly double[] _rowSin = new double[RowCount], _rowCos = new double[RowCount];
    private readonly double[] _wave = new double[RowCount], _ripple = new double[RowCount];
    private readonly double[] _tube = new double[RowCount], _bend = new double[RowCount], _lobe = new double[RowCount];
    public readonly double[] RowAngle = new double[RowCount], RowCosAngle = new double[RowCount], RowSinAngle = new double[RowCount];
    private readonly double[] _rowRadius = new double[RowCount], _rowWidth = new double[RowCount], _rowHeight = new double[RowCount];
    private readonly double[] _colSin = new double[ColumnCount], _colCos = new double[ColumnCount];

    private readonly Drift[] _drifts = new Drift[9];

    /// <summary>每帧输出：粒子位置（未乘 fit）、深度层、所属行。</summary>
    public readonly float[] X = new float[ParticleCount];
    public readonly float[] Y = new float[ParticleCount];
    public readonly byte[] Layer = new byte[ParticleCount];

    /// <summary>每帧输出：9 个噪声通道当前值（-1…1）。</summary>
    public readonly double[] Noise = new double[9];

    /// <summary>本帧整体缩放系数，确保粒子落在留白圆内。</summary>
    public double Fit { get; private set; } = 1;
    public double MaxRadius { get; private set; }

    public static double LayerAlpha(int layer) => 0.08 + layer / 15.0 * 0.31;
    public static double LayerSize(int layer) => 0.58 + layer / 15.0 * 1.02;

    /// <param name="random">
    /// 与参考页共享序列的随机源。参考页先用种子 8217 生成 410 个刻度随机数，再创建漂移通道；
    /// 传入 <see cref="CreateReferenceRandom"/> 可得到完全一致的漂移参数。
    /// </param>
    public ParticleModel(ParkMillerRandom random)
    {
        for (int i = 0; i < RowCount; i++)
        {
            double u = (double)i / RowCount * Math.PI * 2;
            double angle = -0.4 + Math.Sin(u) * 0.5;
            _rowSin[i] = Math.Sin(u); _rowCos[i] = Math.Cos(u);
            _wave[i] = Math.Sin(3 * u + 0.7); _ripple[i] = Math.Cos(5 * u - 0.5);
            _tube[i] = Math.Sin(3 * u + 1.1); _bend[i] = Math.Cos(2 * u + 0.5);
            _lobe[i] = Math.Sin(2 * u + 1.8);
            RowAngle[i] = angle; RowCosAngle[i] = Math.Cos(angle); RowSinAngle[i] = Math.Sin(angle);
        }
        for (int j = 0; j < ColumnCount; j++)
        {
            double v = (double)j / ColumnCount * Math.PI * 2;
            _colSin[j] = Math.Sin(v); _colCos[j] = Math.Cos(v);
        }
        for (int k = 0; k < _drifts.Length; k++) _drifts[k] = new Drift(random);
    }

    /// <summary>参考页刻度生成共消耗 (116 + 89) × 2 = 410 个随机数。</summary>
    public const int ReferenceTickRandomCalls = (116 + 89) * 2;

    public static ParkMillerRandom CreateReferenceRandom()
    {
        var r = new ParkMillerRandom(8217);
        for (int i = 0; i < ReferenceTickRandomCalls; i++) r.Next();
        return r;
    }

    public static int RowOf(int particleIndex) => particleIndex / ColumnCount;

    /// <summary>计算 <paramref name="timeSeconds"/>（未乘播放倍率的粒子时钟）时刻的粒子位置。</summary>
    public void Update(double timeSeconds)
    {
        double t = timeSeconds * PlaybackRate;
        for (int k = 0; k < 9; k++) Noise[k] = _drifts[k].Sample(t);
        var n = Noise;

        double tilt = 0.94 + n[0] * 0.12;
        double twist = 0.22 + n[1] * 0.16;
        double cosTilt = Math.Cos(tilt), sinTilt = Math.Sin(tilt);
        double cosTwist = Math.Cos(twist), sinTwist = Math.Sin(twist);
        double verticalScale = 1.65 + n[2] * 0.12;

        for (int i = 0; i < RowCount; i++)
        {
            _rowRadius[i] = 147 + _wave[i] * (12 + n[3] * 8) + _ripple[i] * (5 + n[4] * 4) + _lobe[i] * n[5] * 9;
            _rowWidth[i] = 59 + _tube[i] * 9 + _bend[i] * n[6] * 8;
            _rowHeight[i] = _bend[i] * (14 + n[7] * 8) + _lobe[i] * n[8] * 7;
        }

        double maxR2 = 0;
        int p = 0;
        for (int i = 0; i < RowCount; i++)
        {
            double rr = _rowRadius[i], rw = _rowWidth[i], rh = _rowHeight[i];
            double rc = _rowCos[i], rs = _rowSin[i];
            for (int j = 0; j < ColumnCount; j++, p++)
            {
                double radius = rr + rw * _colCos[j];
                double x = radius * rc;
                double y = radius * rs;
                double z = rw * _colSin[j] + rh;
                double yy = y * cosTilt - z * sinTilt;
                double zz = y * sinTilt + z * cosTilt;
                double px = (x * cosTwist - yy * sinTwist) * 1.03;
                double py = (x * sinTwist + yy * cosTwist) * verticalScale;
                X[p] = (float)px;
                Y[p] = (float)py;
                double r2 = px * px + py * py;
                if (r2 > maxR2) maxR2 = r2;
                int layer = (int)Math.Floor((zz + 240) / 480 * 16);
                Layer[p] = (byte)(layer < 0 ? 0 : layer > 15 ? 15 : layer);
            }
        }
        MaxRadius = Math.Sqrt(maxR2);
        Fit = MaxRadius > 0 ? Math.Min(1, FitRadius / MaxRadius) : 1;
    }

    /// <summary>
    /// 单个漂移通道：13–27 秒周期内在随机目标之间以五次平滑插值过渡（速度、加速度连续）。
    /// </summary>
    private sealed class Drift
    {
        private readonly double _period, _phase, _salt;

        public Drift(ParkMillerRandom random)
        {
            _period = 13 + random.Next() * 14;
            _phase = random.Next() * _period;
            _salt = random.Next() * 1000;
        }

        private double SampleAt(double index)
        {
            double value = Math.Sin((index + _salt) * 127.1) * 43758.5453;
            return (value - Math.Floor(value)) * 2 - 1;
        }

        public double Sample(double time)
        {
            double position = (time + _phase) / _period;
            double index = Math.Floor(position);
            double f = position - index;
            double blend = f * f * f * (f * (f * 6 - 15) + 10);
            return SampleAt(index) * (1 - blend) + SampleAt(index + 1) * blend;
        }
    }
}
