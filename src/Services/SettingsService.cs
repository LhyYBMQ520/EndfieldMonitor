using System.Text.Json;
using System.Text.Json.Serialization;
using EndfieldMonitor.Models;

namespace EndfieldMonitor.Services;

/// <summary>
/// 绿色版配置：EndfieldMonitor.settings.json 位于 EXE 同目录，自动生成。
/// 原子写入（临时文件 + File.Replace / Move）；损坏时备份原文件并回退默认值。
/// 若 EXE 目录不可写（例如放在 Program Files），回退到 %LOCALAPPDATA%\EndfieldMonitor。
/// </summary>
public sealed class SettingsService
{
    public const string FileName = "EndfieldMonitor.settings.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly object _ioLock = new();
    private System.Threading.Timer? _debounce;
    private AppSettings? _pending;

    public SettingsService(string? directory = null)
    {
        Directory = directory ?? ResolveDirectory();
        FilePath = Path.Combine(Directory, FileName);
    }

    public string Directory { get; }
    public string FilePath { get; }
    public string? LoadDiagnostic { get; private set; }

    private static string ResolveDirectory()
    {
        string exeDir = AppContext.BaseDirectory;
        if (IsWritable(exeDir)) return exeDir;
        string fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EndfieldMonitor");
        System.IO.Directory.CreateDirectory(fallback);
        return fallback;
    }

    private static bool IsWritable(string dir)
    {
        try
        {
            string probe = Path.Combine(dir, ".write-test-" + Environment.ProcessId);
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }

    public AppSettings Load()
    {
        lock (_ioLock)
        {
            if (!File.Exists(FilePath)) return new AppSettings();
            try
            {
                string json = File.ReadAllText(FilePath);
                var s = Parse(json, out string? diag);
                LoadDiagnostic = diag;
                return s;
            }
            catch (Exception ex)
            {
                LoadDiagnostic = "配置读取失败，已使用默认值：" + ex.Message;
                BackupCorrupt();
                return new AppSettings();
            }
        }
    }

    /// <summary>解析与校验（纯函数，便于测试）。</summary>
    public static AppSettings Parse(string json, out string? diagnostic)
    {
        diagnostic = null;
        AppSettings? s;
        try { s = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions); }
        catch (JsonException ex) { throw new InvalidDataException("JSON 格式错误：" + ex.Message, ex); }
        if (s is null) throw new InvalidDataException("配置为空。");
        if (s.Version > AppSettings.CurrentVersion)
            diagnostic = $"配置版本 {s.Version} 高于当前程序支持的 {AppSettings.CurrentVersion}，已按兼容方式读取。";
        if (s.Version < 1) s.Version = 1;
        // 未来版本迁移在此按 Version 逐级处理。
        if (s.Normalize()) diagnostic ??= "配置中有越界值，已自动修正。";
        s.Version = AppSettings.CurrentVersion;
        return s;
    }

    private void BackupCorrupt()
    {
        try
        {
            string bak = FilePath + ".corrupt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            File.Copy(FilePath, bak, overwrite: true);
        }
        catch { }
    }

    /// <summary>防抖保存：频繁修改（拖动、输入名称）合并为一次写盘。</summary>
    public void SaveDebounced(AppSettings settings)
    {
        lock (_ioLock)
        {
            _pending = settings.Clone();
            _debounce ??= new System.Threading.Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
            _debounce.Change(600, Timeout.Infinite);
        }
    }

    public void Flush()
    {
        AppSettings? p;
        lock (_ioLock) { p = _pending; _pending = null; }
        if (p != null) SaveNow(p);
    }

    public void SaveNow(AppSettings settings)
    {
        lock (_ioLock)
        {
            try
            {
                string json = JsonSerializer.Serialize(settings, JsonOptions);
                string tmp = FilePath + ".tmp";
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var w = new StreamWriter(fs, new System.Text.UTF8Encoding(false)))
                {
                    w.Write(json);
                    w.Flush();
                    fs.Flush(true);
                }
                if (File.Exists(FilePath))
                    File.Replace(tmp, FilePath, null, ignoreMetadataErrors: true);
                else
                    File.Move(tmp, FilePath);
            }
            catch (Exception ex)
            {
                LoadDiagnostic = "配置保存失败：" + ex.Message;
            }
        }
    }

    public void Shutdown()
    {
        lock (_ioLock) { _debounce?.Dispose(); _debounce = null; }
        Flush();
    }
}
