using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace AIUsageChecker;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly SettingsStore _store = new();
    private readonly AutoUsageService _autoUsage = new();
    private readonly GeminiWebUsageService _geminiWeb;
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromMinutes(1) };
    private readonly DispatcherTimer _autoRefresh = new() { Interval = TimeSpan.FromMinutes(5) };
    private bool _isRefreshing;

    public ObservableCollection<ProviderUsage> Providers { get; } = [];
    public ObservableCollection<ProviderUsage> VisibleProviders { get; } = [];
    public bool IsNotRefreshing => !_isRefreshing;
    public string RefreshButtonText => _isRefreshing ? "確認中…" : "今すぐ確認";
    public event PropertyChangedEventHandler? PropertyChanged;

    public MainWindow()
    {
        InitializeComponent();
        _geminiWeb = new GeminiWebUsageService(GeminiUsageBrowser);
        DataContext = this;
        Loaded += MainWindow_Loaded;
        _clock.Tick += (_, _) => { foreach (var provider in Providers) provider.Tick(); };
        _autoRefresh.Tick += async (_, _) => await RefreshAllAsync();
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        foreach (var provider in await _store.LoadAsync()) Providers.Add(provider);
        RefreshVisibleProviders();
        _clock.Start();
        _autoRefresh.Start();
        await RefreshAllAsync();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAllAsync();

    private async void Settings_Click(object sender, RoutedEventArgs e)
    {
        var window = new SettingsWindow(Providers) { Owner = this };
        if (window.ShowDialog() != true) return;
        RefreshVisibleProviders();
        await _store.SaveAsync(Providers);
        await RefreshAllAsync();
    }

    private void RefreshVisibleProviders()
    {
        VisibleProviders.Clear();
        foreach (var provider in Providers.Where(item => item.IsVisible)) VisibleProviders.Add(provider);
    }

    private async Task RefreshAllAsync()
    {
        if (_isRefreshing) return;
        _isRefreshing = true;
        OnPropertyChanged(nameof(IsNotRefreshing));
        OnPropertyChanged(nameof(RefreshButtonText));
        try
        {
            var enabledIds = VisibleProviders.Select(item => item.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var usageTask = _autoUsage.RefreshAllAsync(enabledIds.Where(id => !id.Equals("gemini", StringComparison.OrdinalIgnoreCase)));
            var geminiTask = enabledIds.Contains("gemini") ? _geminiWeb.FetchAsync() : null;
            await usageTask;
            foreach (var result in usageTask.Result) ApplyAutoResult(result);
            if (geminiTask is not null)
            {
                var geminiResult = await geminiTask;
                if (!geminiResult.Success)
                {
                    var cliResult = (await _autoUsage.FetchGeminiCliAsync()).FirstOrDefault();
                    if (cliResult is not null && cliResult.Success) geminiResult = cliResult;
                }
                ApplyAutoResult(geminiResult);
            }
            await _store.SaveAsync(Providers);
        }
        catch
        {
            // Keep the last successful values when a provider or network is temporarily unavailable.
        }
        finally
        {
            _isRefreshing = false;
            OnPropertyChanged(nameof(IsNotRefreshing));
            OnPropertyChanged(nameof(RefreshButtonText));
        }
    }

    private void ApplyAutoResult(ProviderAutoResult result)
    {
        var provider = Providers.FirstOrDefault(item => item.Id == result.ProviderId);
        if (provider is null) return;
        provider.AutoDetail = result.Detail;
        if (!result.Success) return;
        provider.Used = result.Used;
        provider.Limit = result.Limit;
        provider.Unit = "%";
        provider.ResetAt = result.ResetAt;
        provider.WindowLabel = result.WindowLabel;
        provider.LastAutoChecked = DateTimeOffset.Now;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
