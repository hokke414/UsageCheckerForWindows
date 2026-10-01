using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Windows.Media;

namespace AIUsageChecker;

public sealed class ProviderUsage : INotifyPropertyChanged
{
    private decimal? _used;
    private decimal? _limit;
    private DateTimeOffset? _resetAt;
    private string _note = "";
    private string _windowLabel = "";
    private string _autoStatus = "未確認";
    private string _autoDetail = "";
    private DateTimeOffset? _lastAutoChecked;
    private bool _isVisible = true;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string ShortName { get; set; } = "";
    public string Subtitle { get; set; } = "";
    public string Accent { get; set; } = "#8B72FF";
    public string Unit { get; set; } = "回";
    public string WindowLabel
    {
        get => _windowLabel;
        set { _windowLabel = value; OnPropertyChanged(); OnPropertyChanged(nameof(RemainingDisplay)); }
    }
    public string UsageUrl { get; set; } = "";
    public string HelpUrl { get; set; } = "";
    public string Guidance { get; set; } = "";
    public bool IsVisible
    {
        get => _isVisible;
        set { _isVisible = value; OnPropertyChanged(); }
    }
    public string AutoStatus
    {
        get => _autoStatus;
        set { _autoStatus = value; OnPropertyChanged(); OnPropertyChanged(nameof(SourceDisplay)); OnPropertyChanged(nameof(SourceBrush)); }
    }
    public string AutoDetail
    {
        get => _autoDetail;
        set { _autoDetail = value; OnPropertyChanged(); }
    }
    public DateTimeOffset? LastAutoChecked
    {
        get => _lastAutoChecked;
        set { _lastAutoChecked = value; OnPropertyChanged(); OnPropertyChanged(nameof(SourceDisplay)); }
    }

    public decimal? Used
    {
        get => _used;
        set { _used = value; NotifyUsageChanged(); }
    }

    public decimal? Limit
    {
        get => _limit;
        set { _limit = value; NotifyUsageChanged(); }
    }

    public DateTimeOffset? ResetAt
    {
        get => _resetAt;
        set { _resetAt = value; OnPropertyChanged(); OnPropertyChanged(nameof(ResetDisplay)); }
    }

    public string Note
    {
        get => _note;
        set { _note = value; OnPropertyChanged(); }
    }

    [JsonIgnore] public bool HasMeasurement => Used.HasValue && Limit is > 0;
    [JsonIgnore] public double Percent => HasMeasurement ? Math.Clamp((double)(Used!.Value / Limit!.Value * 100), 0, 100) : 0;
    [JsonIgnore] public string PercentDisplay => HasMeasurement ? $"{Percent:0}%" : "未記録";
    [JsonIgnore] public string UsageDisplay => HasMeasurement ? $"{Used:0.##} / {Limit:0.##} {Unit}" : "使用量を記録してください";
    [JsonIgnore] public string RemainingDisplay => HasMeasurement ? $"残り {Math.Max(0, Limit!.Value - Used!.Value):0.##} {Unit}" : WindowLabel;
    [JsonIgnore] public string State => !HasMeasurement ? "要記録" : Percent >= 90 ? "上限間近" : Percent >= 70 ? "注意" : "余裕あり";
    [JsonIgnore] public Brush AccentBrush => BrushFrom(Accent, "#8B72FF");
    [JsonIgnore] public Brush StateBrush => State switch
    {
        "上限間近" => BrushFrom("#B4002A", "#B4002A"),
        "注意" => BrushFrom("#8A4B00", "#8A4B00"),
        "余裕あり" => BrushFrom("#006E54", "#006E54"),
        _ => BrushFrom("#626264", "#626264")
    };
    [JsonIgnore] public string ResetDisplay => ResetAt is null
        ? "更新時刻は未設定"
        : ResetAt <= DateTimeOffset.Now
            ? "更新時刻を過ぎています"
            : $"更新まで {FormatDuration(ResetAt.Value - DateTimeOffset.Now)}";
    [JsonIgnore] public string SourceDisplay => AutoStatus == "自動"
        ? $"自動 · {LastAutoChecked?.LocalDateTime:HH:mm}"
        : AutoStatus;
    [JsonIgnore] public Brush SourceBrush => AutoStatus == "自動"
        ? BrushFrom("#006E54", "#006E54")
        : AutoStatus == "取得失敗"
            ? BrushFrom("#B4002A", "#B4002A")
            : BrushFrom("#626264", "#626264");

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Tick()
    {
        OnPropertyChanged(nameof(ResetDisplay));
        OnPropertyChanged(nameof(SourceDisplay));
    }

    private void NotifyUsageChanged()
    {
        OnPropertyChanged();
        OnPropertyChanged(nameof(HasMeasurement));
        OnPropertyChanged(nameof(Percent));
        OnPropertyChanged(nameof(PercentDisplay));
        OnPropertyChanged(nameof(UsageDisplay));
        OnPropertyChanged(nameof(RemainingDisplay));
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(StateBrush));
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalDays >= 1) return $"{(int)duration.TotalDays}日 {duration.Hours}時間";
        if (duration.TotalHours >= 1) return $"{(int)duration.TotalHours}時間 {duration.Minutes}分";
        return $"{Math.Max(1, duration.Minutes)}分";
    }

    private static Brush BrushFrom(string value, string fallback)
    {
        try { return (Brush)new BrushConverter().ConvertFromString(value)!; }
        catch { return (Brush)new BrushConverter().ConvertFromString(fallback)!; }
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
