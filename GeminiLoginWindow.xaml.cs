using Microsoft.Web.WebView2.Core;
using System.Text.Json;
using System.Windows;

namespace AIUsageChecker;

public partial class GeminiLoginWindow : Window
{
    public GeminiLoginWindow()
    {
        InitializeComponent();
        Loaded += GeminiLoginWindow_Loaded;
    }

    private async void GeminiLoginWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await Browser.EnsureCoreWebView2Async(await WebViewProfiles.GeminiAsync());
            Browser.CoreWebView2.Navigate("https://gemini.google.com/usage?hl=en");
        }
        catch (Exception ex) { StatusText.Text = $"ログイン画面を開けません: {ex.Message}"; }
    }

    private async void Browser_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!e.IsSuccess || Browser.CoreWebView2 is null) return;
        try
        {
            await Task.Delay(1200);
            var json = await Browser.CoreWebView2.ExecuteScriptAsync("document.body ? document.body.innerText : ''");
            var text = JsonSerializer.Deserialize<string>(json) ?? "";
            if (text.Contains("Current usage", StringComparison.OrdinalIgnoreCase) || text.Contains("Weekly limit", StringComparison.OrdinalIgnoreCase))
                StatusText.Text = "接続できました。［完了］を押してください。";
        }
        catch { }
    }

    private void Complete_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
