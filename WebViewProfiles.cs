using Microsoft.Web.WebView2.Core;
using System.IO;

namespace AIUsageChecker;

public static class WebViewProfiles
{
    private static readonly Lazy<Task<CoreWebView2Environment>> NotionEnvironment = new(() => CreateAsync("Notion"));
    private static readonly Lazy<Task<CoreWebView2Environment>> GeminiEnvironment = new(() => CreateAsync("Gemini"));

    public static Task<CoreWebView2Environment> NotionAsync() => NotionEnvironment.Value;
    public static Task<CoreWebView2Environment> GeminiAsync() => GeminiEnvironment.Value;
    public static string GeminiDataPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AIUsageChecker", "WebView2", "Gemini");

    private static Task<CoreWebView2Environment> CreateAsync(string provider) =>
        CoreWebView2Environment.CreateAsync(userDataFolder: EnsureDataPath(provider));

    private static string EnsureDataPath(string provider)
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AIUsageChecker", "WebView2", provider);
        Directory.CreateDirectory(path);
        return path;
    }
}
