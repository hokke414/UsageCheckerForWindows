using Microsoft.Web.WebView2.Core;
using System.Windows;

namespace AIUsageChecker;

public partial class NotionLoginWindow : Window
{
    private bool _checking;

    public NotionLoginWindow()
    {
        InitializeComponent();
        Loaded += NotionLoginWindow_Loaded;
    }

    private async void NotionLoginWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await Browser.EnsureCoreWebView2Async(await WebViewProfiles.NotionAsync());
            Browser.CoreWebView2.Navigate("https://app.notion.com");
        }
        catch (Exception ex) { StatusText.Text = $"ログイン画面を開けません: {ex.Message}"; }
    }

    private async void Browser_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (_checking || !e.IsSuccess || Browser.CoreWebView2 is null) return;
        _checking = true;
        try
        {
            var cookies = await Browser.CoreWebView2.CookieManager.GetCookiesAsync("https://app.notion.com");
            var token = cookies.FirstOrDefault(cookie => cookie.Name.Equals("token_v2", StringComparison.OrdinalIgnoreCase));
            if (token is null || string.IsNullOrWhiteSpace(token.Value)) return;
            new ConnectionSettingsStore().SaveNotionToken(token.Value);
            StatusText.Text = "接続できました。";
            DialogResult = true;
        }
        catch (Exception ex) { StatusText.Text = $"接続を確認できません: {ex.Message}"; }
        finally { _checking = false; }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
