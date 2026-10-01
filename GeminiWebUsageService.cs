using Microsoft.Web.WebView2.Wpf;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AIUsageChecker;

public sealed class GeminiWebUsageService(WebView2 browser)
{
    private readonly WebView2 _browser = browser;

    public async Task<ProviderAutoResult> FetchAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _browser.EnsureCoreWebView2Async(await WebViewProfiles.GeminiAsync());
            var navigation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            void Completed(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2NavigationCompletedEventArgs args) => navigation.TrySetResult(args.IsSuccess);
            _browser.NavigationCompleted += Completed;
            try
            {
                _browser.CoreWebView2.Navigate("https://gemini.google.com/usage?hl=en");
                await navigation.Task.WaitAsync(TimeSpan.FromSeconds(12), cancellationToken);
            }
            finally { _browser.NavigationCompleted -= Completed; }

            for (var attempt = 0; attempt < 20; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var json = await _browser.CoreWebView2.ExecuteScriptAsync("document.body ? document.body.innerText : ''");
                var text = JsonSerializer.Deserialize<string>(json) ?? "";
                var result = Parse(text);
                if (result is not null) return result;
                if (_browser.Source?.Host.Contains("accounts.google", StringComparison.OrdinalIgnoreCase) == true)
                    return Missing("設定からGeminiにログインしてください。");
                await Task.Delay(500, cancellationToken);
            }
            return Missing("Gemini使用量を読み取れませんでした。設定からログインし直すか、公式Usage画面を確認してください。");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new("gemini", false, null, null, null, "", $"Gemini Web: {ex.GetType().Name} - {ex.Message}", "取得失敗");
        }
    }

    internal static ProviderAutoResult? Parse(string text)
    {
        var clean = Regex.Replace(text, @"\s+", " ");
        var current = Regex.Match(clean, @"Current usage.*?(\d+(?:\.\d+)?)\s*%\s*used", RegexOptions.IgnoreCase);
        var weekly = Regex.Match(clean, @"Weekly limit.*?(\d+(?:\.\d+)?)\s*%\s*used", RegexOptions.IgnoreCase);
        if (!current.Success && !weekly.Success) return null;
        var windows = new List<(string Label, decimal Used, DateTimeOffset? Reset)>();
        if (current.Success)
        {
            var reset = Regex.Match(clean, @"Current usage.*?Resets at\s*(\d{1,2}:\d{2}\s*(?:AM|PM))", RegexOptions.IgnoreCase);
            windows.Add(("5時間", decimal.Parse(current.Groups[1].Value, CultureInfo.InvariantCulture), ParseTime(reset.Success ? reset.Groups[1].Value : null)));
        }
        if (weekly.Success)
        {
            var reset = Regex.Match(clean, @"Weekly limit.*?Resets\s*([A-Za-z]{3}\s*\d{1,2}\s*at\s*\d{1,2}:\d{2}\s*(?:AM|PM))", RegexOptions.IgnoreCase);
            windows.Add(("週次", decimal.Parse(weekly.Groups[1].Value, CultureInfo.InvariantCulture), ParseWeekly(reset.Success ? reset.Groups[1].Value : null)));
        }
        var tightest = windows.MaxBy(item => item.Used);
        return new("gemini", true, tightest.Used, 100, tightest.Reset, tightest.Label,
            string.Join(" / ", windows.Select(item => $"{item.Label} {item.Used:0.#}%")) + " · Gemini Apps");
    }

    private static DateTimeOffset? ParseTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !DateTime.TryParse(value, CultureInfo.GetCultureInfo("en-US"), DateTimeStyles.None, out var time)) return null;
        var result = new DateTimeOffset(DateTime.Today.Add(time.TimeOfDay), TimeZoneInfo.Local.GetUtcOffset(DateTime.Now));
        return result <= DateTimeOffset.Now ? result.AddDays(1) : result;
    }

    private static DateTimeOffset? ParseWeekly(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Replace(" at ", " ", StringComparison.OrdinalIgnoreCase);
        if (!DateTime.TryParse(normalized, CultureInfo.GetCultureInfo("en-US"), DateTimeStyles.AllowWhiteSpaces, out var date)) return null;
        var candidate = new DateTimeOffset(new DateTime(DateTime.Now.Year, date.Month, date.Day, date.Hour, date.Minute, 0), TimeZoneInfo.Local.GetUtcOffset(DateTime.Now));
        return candidate <= DateTimeOffset.Now.AddDays(-1) ? candidate.AddYears(1) : candidate;
    }

    private static ProviderAutoResult Missing(string detail) => new("gemini", false, null, null, null, "", detail, "要設定");
}
