using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using EndfieldMonitor.Models;
using EndfieldMonitor.Services;

namespace EndfieldMonitor.ViewModels;

/// <summary>设置面板中的一个传感器条目。数值原地更新，不重建列表。</summary>
public sealed class SensorItem : ObservableObject
{
    public SensorItem(SensorReading r)
    {
        Id = r.Id;
        HardwareName = r.HardwareName;
        HardwareType = r.HardwareType;
        Name = r.Name;
        Kind = r.Kind;
        Group = $"{SensorSelection.DeviceGroupName(r.HardwareType)} · {r.HardwareName}";
        KindText = SensorFormatting.KindDisplayName(r.Kind);
        Reading = r;
        _valueText = SensorFormatting.Describe(r);
    }

    public string Id { get; }
    public string HardwareName { get; }
    public string HardwareType { get; }
    public string Name { get; }
    public SensorKind Kind { get; }
    public string Group { get; }
    public string KindText { get; }
    public SensorReading Reading { get; private set; }
    public string Label => SensorSelection.Label(Reading);
    public string AutomationName => $"{Group}，{Name}，{KindText}，{ValueText}";

    private string _valueText;
    public string ValueText { get => _valueText; private set => Set(ref _valueText, value); }

    public void Update(SensorReading r)
    {
        Reading = r;
        ValueText = SensorFormatting.Describe(r);
    }
}

/// <summary>两个槽位共享的传感器目录。</summary>
public sealed class SensorCatalog
{
    private readonly Dictionary<string, SensorItem> _byId = new(StringComparer.Ordinal);
    private int _structureHash;

    public ObservableCollection<SensorItem> Items { get; } = new();

    public SensorItem? Find(string? id) => id != null && _byId.TryGetValue(id, out var item) ? item : null;

    /// <summary>结构变化时增删条目，其余只原地更新数值。</summary>
    public void Update(TelemetrySnapshot snapshot)
    {
        int hash = snapshot.StructureHash;
        if (hash != _structureHash)
        {
            _structureHash = hash;
            var present = new HashSet<string>(StringComparer.Ordinal);
            int index = 0;
            foreach (var r in snapshot.Sensors)
            {
                if (r.Id.Length == 0 || !present.Add(r.Id)) continue;
                if (!_byId.ContainsKey(r.Id))
                {
                    var item = new SensorItem(r);
                    _byId[r.Id] = item;
                    Items.Insert(Math.Min(index, Items.Count), item);
                }
                index++;
            }
            for (int i = Items.Count - 1; i >= 0; i--)
            {
                if (!present.Contains(Items[i].Id))
                {
                    _byId.Remove(Items[i].Id);
                    Items.RemoveAt(i);
                }
            }
        }
        foreach (var r in snapshot.Sensors)
            if (_byId.TryGetValue(r.Id, out var item)) item.Update(r);
    }
}

/// <summary>单个槽位的设置。</summary>
public sealed class SlotSettingsViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly SensorCatalog _catalog;
    private bool _syncing;

    public SlotSettingsViewModel(MainViewModel main, SensorCatalog catalog, int index)
    {
        _main = main;
        _catalog = catalog;
        Index = index;
        View = new ListCollectionView(catalog.Items);
        View.GroupDescriptions.Add(new PropertyGroupDescription(nameof(SensorItem.Group)));
        View.Filter = o => o is SensorItem s && SensorSelection.Matches(s.Reading, _search);
    }

    public int Index { get; }
    public string Header => Index == 0 ? "上方监控项" : "下方监控项";
    public ListCollectionView View { get; }
    public IReadOnlyList<int> DecimalOptions { get; } = new[] { 0, 1, 2 };

    private SlotConfig Config => _main.Settings[Index];

    private string _search = "";
    public string Search
    {
        get => _search;
        set
        {
            if (Set(ref _search, value ?? ""))
                View.Refresh();
        }
    }

    private SensorItem? _selected;
    public SensorItem? Selected
    {
        get => _selected;
        set
        {
            // 过滤导致 ListBox 暂时失去选中项时不清除已保存的选择。
            if (value == null && _selected != null && !_syncing) return;
            if (!Set(ref _selected, value) || _syncing || value == null) return;
            _main.SetSlotSensor(Index, value.Reading);
            RefreshStatus();
        }
    }

    public string CustomName
    {
        get => Config.CustomName ?? "";
        set
        {
            if (_syncing) return;
            _main.SetSlotName(Index, value);
            Raise();
            Raise(nameof(NamePlaceholder));
        }
    }

    public string NamePlaceholder => "默认：" + SensorSelection.DefaultName(_catalog.Find(Config.SensorId)?.Reading, Index);

    public int Decimals
    {
        get => Config.Decimals;
        set
        {
            if (_syncing) return;
            _main.SetSlotDecimals(Index, value);
            Raise();
        }
    }

    private string _selectionText = "";
    public string SelectionText { get => _selectionText; private set => Set(ref _selectionText, value); }

    private bool _selectionMissing;
    public bool SelectionMissing { get => _selectionMissing; private set => Set(ref _selectionMissing, value); }

    /// <summary>配置被外部改变（恢复默认等）后同步界面。</summary>
    public void SyncFromConfig()
    {
        _syncing = true;
        try
        {
            var item = _catalog.Find(Config.SensorId);
            _selected = item;
            Raise(nameof(Selected));
            Raise(nameof(CustomName));
            Raise(nameof(Decimals));
            Raise(nameof(NamePlaceholder));
        }
        finally { _syncing = false; }
        RefreshStatus();
    }

    public void RefreshStatus()
    {
        var cfg = Config;
        if (cfg.SensorId == null)
        {
            SelectionText = "尚未选择监控项";
            SelectionMissing = true;
            return;
        }
        var item = _catalog.Find(cfg.SensorId);
        if (item == null)
        {
            SelectionText = $"传感器不可用：{cfg.SensorLabel ?? cfg.SensorId}";
            SelectionMissing = true;
            return;
        }
        if (!ReferenceEquals(_selected, item))
        {
            _syncing = true;
            try { _selected = item; Raise(nameof(Selected)); }
            finally { _syncing = false; }
        }
        SelectionText = $"{item.Group} › {item.Name}　{SensorFormatting.Describe(item.Reading, cfg.Decimals)}";
        SelectionMissing = false;
    }
}

/// <summary>设置面板整体。</summary>
public sealed class SettingsViewModel : ObservableObject
{
    private readonly MainViewModel _main;

    public SettingsViewModel(MainViewModel main)
    {
        _main = main;
        Top = new SlotSettingsViewModel(main, main.Catalog, 0);
        Bottom = new SlotSettingsViewModel(main, main.Catalog, 1);
        ResetSlotsCommand = new RelayCommand(() => { _main.ResetSlotsToDefault(); SyncAll(); });
        RestartAsAdminCommand = new RelayCommand(() => _main.RequestRestartAsAdmin(), () => !_main.IsAdmin);
        ScaleOptions = AppSettings.ScaleSteps.Select(s => new ScaleOption(s)).ToArray();
        main.PropertyChanged += OnMainChanged;
    }

    public SlotSettingsViewModel Top { get; }
    public SlotSettingsViewModel Bottom { get; }
    public RelayCommand ResetSlotsCommand { get; }
    public RelayCommand RestartAsAdminCommand { get; }
    public IReadOnlyList<ScaleOption> ScaleOptions { get; }

    public sealed record ScaleOption(double Value)
    {
        public string Text => $"{Value * 100:0}%";
        public override string ToString() => Text;
    }

    public ScaleOption SelectedScale
    {
        get => ScaleOptions.FirstOrDefault(o => Math.Abs(o.Value - _main.Scale) < 0.001) ?? ScaleOptions[2];
        set { if (value != null) _main.SetScale(value.Value); }
    }

    public bool ParticlesEnabled { get => _main.ParticlesEnabled; set => _main.SetParticles(value); }

    public string CollectorText => _main.CollectorText;
    public string LastUpdateText => _main.LastUpdateText;
    public string PrivilegeText => _main.PrivilegeText;
    public string ErrorText => _main.ErrorText;
    public bool HasErrors => !string.IsNullOrEmpty(_main.ErrorText);
    public string ConfigPath => _main.ConfigPath;
    public string PerformanceText => _main.PerformanceText;
    public bool IsAdmin => _main.IsAdmin;

    private void OnMainChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.Scale): Raise(nameof(SelectedScale)); break;
            case nameof(MainViewModel.ParticlesEnabled): Raise(nameof(ParticlesEnabled)); break;
            case nameof(MainViewModel.CollectorText): Raise(nameof(CollectorText)); break;
            case nameof(MainViewModel.LastUpdateText): Raise(nameof(LastUpdateText)); break;
            case nameof(MainViewModel.PrivilegeText): Raise(nameof(PrivilegeText)); break;
            case nameof(MainViewModel.PerformanceText): Raise(nameof(PerformanceText)); break;
            case nameof(MainViewModel.ErrorText): Raise(nameof(ErrorText)); Raise(nameof(HasErrors)); break;
        }
    }

    /// <summary>面板打开或数据刷新时调用。</summary>
    public void SyncAll()
    {
        Top.SyncFromConfig();
        Bottom.SyncFromConfig();
    }

    public void RefreshValues()
    {
        Top.RefreshStatus();
        Bottom.RefreshStatus();
    }
}
