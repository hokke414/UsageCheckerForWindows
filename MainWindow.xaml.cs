using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
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
    private ProviderUsage? _selectedProvider;
    private bool _isRefreshing;
    private string _connectionText = "接続を確認中";
    private Brush _connectionBrush = Brushes.Gray;
    private string _lastCheckedText = "まだ確認していません";
    private string _editUsed = "";
    private string _editLimit = "";
    private string _editUnit = "";
    private string _editResetAt = "";
    private string _editNote = "";
    private string _editorError = "";
    private Brush _editorMessageBrush = BrushFrom("#B4002A");

    public ObservableCollection<ProviderUsage> Providers { get; } = [];
    public ObservableCollection<ProviderUsage> VisibleProviders { get; } = [];
    public bool IsNotRefreshing => !_isRefreshing;
    public string RefreshButtonText => _isRefreshing ? "確認中…" : "今すぐ確認";
    public string ConnectionText { get => _connectionText; private set { _connectionText = value; OnPropertyChanged(); } }
    public Brush ConnectionBrush { get => _connectionBrush; private set { _connectionBrush = value; OnPropertyChanged(); } }
    public string LastCheckedText { get => _lastCheckedText; private set { _lastCheckedText = value; OnPropertyChanged(); } }
    public bool HasSelection => _selectedProvider is not null;
    public string EditorTitle => _selectedProvider?.Name ?? "カードの［記録する］を選択";
    public string EditorGuidance => _selectedProvider?.Guidance ?? "公式画面で表示された値を、そのまま記録できます。";
    public string EditUsed { get => _editUsed; set { _editUsed = value; OnPropertyChanged(); } }
    public string EditLimit { get => _editLimit; set { _editLimit = value; OnPropertyChanged(); } }
    public string EditUnit { get => _editUnit; set { _editUnit = value; OnPropertyChanged(); } }
    public string EditResetAt { get => _editResetAt; set { _editResetAt = value; OnPropertyChanged(); } }
    public string EditNote { get => _editNote; set { _editNote = value; OnPropertyChanged(); } }
    public string EditorError { get => _editorError; set { _editorError = value; OnPropertyChanged(); } }
    public Brush EditorMessageBrush { get => _editorMessageBrush; private set { _editorMessageBrush = value; OnPropertyChanged(); } }

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
        SelectProvider(VisibleProviders.FirstOrDefault());
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
        if (_selectedProvider is null || !_selectedProvider.IsVisible) SelectProvider(VisibleProviders.FirstOrDefault());
        await _store.SaveAsync(Providers);
        await RefreshAllAsync();
    }

    private void RefreshVisibleProviders()
    {
        VisibleProviders.Clear();
        foreach (var provider in Providers.Where(item => item.IsVisible)) VisibleProviders.Add(provider);
    }

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is ProviderUsage provider) SelectProvider(provider);
    }

    private void OpenUsage_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is ProviderUsage provider) OpenUrl(provider.UsageUrl);
    }

    private void OpenHelp_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedProvider is not null) OpenUrl(_selectedProvider.HelpUrl);
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedProvider is null) return;
        SetEditorMessage("");

        if (!TryDecimal(EditUsed, out var used) || !TryDecimal(EditLimit, out var limit) || limit <= 0 || used < 0)
        {
            SetEditorMessage("使用済みと上限には0以上の数値を入力し、上限は0より大きくしてください。");
            return;
        }

        DateTimeOffset? resetAt = null;
        if (!string.IsNullOrWhiteSpace(EditResetAt))
        {
            if (!DateTimeOffset.TryParse(EditResetAt, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out var parsed))
            {
                SetEditorMessage("更新日時を「2026/10/01 18:30」の形式で入力してください。");
                return;
            }
            resetAt = parsed;
        }

        _selectedProvider.Used = used;
        _selectedProvider.Limit = limit;
        _selectedProvider.Unit = string.IsNullOrWhiteSpace(EditUnit) ? "%" : EditUnit.Trim();
        _selectedProvider.ResetAt = resetAt;
        _selectedProvider.Note = EditNote.Trim();

        try
        {
            await _store.SaveAsync(Providers);
            SetEditorMessage("保存しました。", true);
        }
        catch (Exception ex)
        {
            SetEditorMessage($"保存できませんでした: {ex.Message}");
        }
    }

    private void SelectProvider(ProviderUsage? provider)
    {
        _selectedProvider = provider;
        EditUsed = provider?.Used?.ToString("0.##", CultureInfo.CurrentCulture) ?? "";
        EditLimit = provider?.Limit?.ToString("0.##", CultureInfo.CurrentCulture) ?? "";
        EditUnit = provider?.Unit ?? "";
        EditResetAt = provider?.ResetAt?.LocalDateTime.ToString("yyyy/MM/dd HH:mm") ?? "";
        EditNote = provider?.Note ?? "";
        SetEditorMessage("");
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(EditorTitle));
        OnPropertyChanged(nameof(EditorGuidance));
    }

    private async Task RefreshAllAsync()
    {
        if (_isRefreshing) return;
        _isRefreshing = true;
        OnPropertyChanged(nameof(IsNotRefreshing));
        OnPropertyChanged(nameof(RefreshButtonText));
        try
        {
            ConnectionText = "自動取得中";
            ConnectionBrush = BrushFrom("#8A4B00");
            var connectionTask = CheckConnectionCoreAsync();
            var enabledIds = VisibleProviders.Select(item => item.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var usageTask = _autoUsage.RefreshAllAsync(enabledIds.Where(id => !id.Equals("gemini", StringComparison.OrdinalIgnoreCase)));
            var geminiTask = enabledIds.Contains("gemini") ? _geminiWeb.FetchAsync() : null;
            await Task.WhenAll(connectionTask, usageTask);
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
        catch (Exception ex)
        {
            ConnectionText = $"更新エラー — {ex.GetType().Name}";
            ConnectionBrush = BrushFrom("#B4002A");
        }
        finally
        {
            LastCheckedText = $"最終確認 {DateTime.Now:HH:mm}";
            _isRefreshing = false;
            OnPropertyChanged(nameof(IsNotRefreshing));
            OnPropertyChanged(nameof(RefreshButtonText));
        }
    }

    private async Task CheckConnectionCoreAsync()
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
            using var response = await client.GetAsync("https://www.google.com/generate_204", HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode) throw new HttpRequestException();
            ConnectionText = "オンライン";
            ConnectionBrush = BrushFrom("#006E54");
        }
        catch
        {
            ConnectionText = "オフライン — キャッシュを表示";
            ConnectionBrush = BrushFrom("#B4002A");
        }
    }

    private void ApplyAutoResult(ProviderAutoResult result)
    {
        var provider = Providers.FirstOrDefault(item => item.Id == result.ProviderId);
        if (provider is null) return;
        provider.AutoStatus = result.Status;
        provider.AutoDetail = result.Detail;
        if (!result.Success) return;
        provider.Used = result.Used;
        provider.Limit = result.Limit;
        provider.Unit = "%";
        provider.ResetAt = result.ResetAt;
        provider.WindowLabel = result.WindowLabel;
        provider.Note = result.Detail;
        provider.LastAutoChecked = DateTimeOffset.Now;
        if (_selectedProvider?.Id == provider.Id) SelectProvider(provider);
    }

    private static bool TryDecimal(string text, out decimal result) =>
        decimal.TryParse(text, NumberStyles.Number, CultureInfo.CurrentCulture, out result) ||
        decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out result);

    private static void OpenUrl(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }

    private static Brush BrushFrom(string value) => (Brush)new BrushConverter().ConvertFromString(value)!;
    private void SetEditorMessage(string message, bool isSuccess = false)
    {
        EditorMessageBrush = BrushFrom(isSuccess ? "#006E54" : "#B4002A");
        EditorError = message;
    }
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
