using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;

namespace AIUsageChecker;

public partial class SettingsWindow : Window, INotifyPropertyChanged
{
    private readonly IEnumerable<ProviderUsage> _providers;
    private readonly ConnectionSettingsStore _connectionStore = new();
    private string _notionWorkspaceId = "";
    private string _notionStatus = "未接続";
    private string _geminiStatus = "ログイン情報なし";
    private string _errorText = "";

    public ObservableCollection<ProviderOption> ProviderOptions { get; } = [];
    public string NotionWorkspaceId { get => _notionWorkspaceId; set { _notionWorkspaceId = value; OnPropertyChanged(); } }
    public string NotionStatus { get => _notionStatus; set { _notionStatus = value; OnPropertyChanged(); } }
    public string GeminiStatus { get => _geminiStatus; set { _geminiStatus = value; OnPropertyChanged(); } }
    public string ErrorText { get => _errorText; set { _errorText = value; OnPropertyChanged(); } }
    public event PropertyChangedEventHandler? PropertyChanged;

    public SettingsWindow(IEnumerable<ProviderUsage> providers)
    {
        InitializeComponent();
        _providers = providers;
        foreach (var provider in providers)
            ProviderOptions.Add(new(provider.Id, provider.Name, provider.IsVisible));
        DataContext = this;
        Loaded += SettingsWindow_Loaded;
    }

    private async void SettingsWindow_Loaded(object sender, RoutedEventArgs e)
    {
        var settings = await _connectionStore.LoadAsync();
        NotionWorkspaceId = settings.NotionWorkspaceId;
        NotionStatus = _connectionStore.LoadNotionToken() is null ? "未接続" : "接続情報を保存済み";
        var geminiPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini", "oauth_creds.json");
        var webProfile = Directory.Exists(WebViewProfiles.GeminiDataPath);
        GeminiStatus = webProfile ? "Geminiログイン用プロファイルを検出" : File.Exists(geminiPath) ? "Gemini CLIログインを検出" : "ログインが必要";
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        ErrorText = "";
        try
        {
            if (!string.IsNullOrWhiteSpace(NotionTokenBox.Password))
                _connectionStore.SaveNotionToken(NotionTokenBox.Password);
            await _connectionStore.SaveAsync(new ConnectionSettings { NotionWorkspaceId = NotionWorkspaceId.Trim() });
            foreach (var option in ProviderOptions)
            {
                var provider = _providers.First(item => item.Id == option.Id);
                provider.IsVisible = option.IsVisible;
            }
            DialogResult = true;
        }
        catch (Exception ex) { ErrorText = ex.Message; }
    }

    private void DisconnectNotion_Click(object sender, RoutedEventArgs e)
    {
        _connectionStore.RemoveNotionToken();
        NotionTokenBox.Clear();
        NotionStatus = "未接続";
    }

    private static void OpenUrl(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    private void LoginNotion_Click(object sender, RoutedEventArgs e)
    {
        var login = new NotionLoginWindow { Owner = this };
        if (login.ShowDialog() == true) NotionStatus = "接続情報を保存済み";
    }
    private void OpenNotion_Click(object sender, RoutedEventArgs e) => OpenUrl("https://app.notion.com");
    private void LoginGemini_Click(object sender, RoutedEventArgs e)
    {
        var login = new GeminiLoginWindow { Owner = this };
        if (login.ShowDialog() == true) GeminiStatus = "Gemini Appsへ接続済み";
    }
    private void OpenGemini_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            using var probe = Process.Start(new ProcessStartInfo("where.exe", "gemini")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            probe?.WaitForExit(1500);
            if (probe is null || !probe.HasExited || probe.ExitCode != 0)
            {
                OpenUrl("https://github.com/google-gemini/gemini-cli");
                GeminiStatus = "Gemini CLIが未導入です。インストール案内を開きました。";
                return;
            }
            Process.Start(new ProcessStartInfo("cmd.exe", "/k gemini") { UseShellExecute = true });
        }
        catch { OpenUrl("https://github.com/google-gemini/gemini-cli"); }
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class ProviderOption(string id, string name, bool isVisible) : INotifyPropertyChanged
{
    private bool _isVisible = isVisible;
    public string Id { get; } = id;
    public string Name { get; } = name;
    public bool IsVisible { get => _isVisible; set { _isVisible = value; PropertyChanged?.Invoke(this, new(nameof(IsVisible))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
}
