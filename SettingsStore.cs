using System.Text.Json;
using System.IO;

namespace AIUsageChecker;

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _filePath;

    public SettingsStore()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AIUsageChecker");
        Directory.CreateDirectory(directory);
        _filePath = Path.Combine(directory, "usage.json");
    }

    public async Task<List<ProviderUsage>> LoadAsync()
    {
        if (!File.Exists(_filePath)) return Defaults();
        try
        {
            await using var stream = File.OpenRead(_filePath);
            var saved = await JsonSerializer.DeserializeAsync<List<ProviderUsage>>(stream, JsonOptions);
            return MergeWithDefaults(saved ?? []);
        }
        catch
        {
            return Defaults();
        }
    }

    public async Task SaveAsync(IEnumerable<ProviderUsage> providers)
    {
        var temporaryPath = _filePath + ".tmp";
        await using (var stream = File.Create(temporaryPath))
            await JsonSerializer.SerializeAsync(stream, providers, JsonOptions);
        File.Move(temporaryPath, _filePath, true);
    }

    private static List<ProviderUsage> MergeWithDefaults(List<ProviderUsage> saved)
    {
        var defaults = Defaults();
        foreach (var current in saved)
        {
            var latest = defaults.FirstOrDefault(item => item.Id == current.Id);
            if (latest is null) continue;
            // v1.3 removed manual records. Keep only values confirmed by an automatic check.
            if (current.LastAutoChecked.HasValue)
            {
                latest.Used = current.Used;
                latest.Limit = current.Limit;
                latest.ResetAt = current.ResetAt;
            }
            latest.Unit = string.IsNullOrWhiteSpace(current.Unit) ? latest.Unit : current.Unit;
            latest.WindowLabel = string.IsNullOrWhiteSpace(current.WindowLabel) ? latest.WindowLabel : current.WindowLabel;
            latest.AutoDetail = current.AutoDetail;
            latest.LastAutoChecked = current.LastAutoChecked;
            latest.IsVisible = current.IsVisible;
            latest.IsBalanceOnly = current.IsBalanceOnly;
            latest.ShowProgress = current.ShowProgress;
        }
        return defaults;
    }

    private static List<ProviderUsage> Defaults() =>
    [
        new() { Id="notion", Name="Notion AI", Accent="#1A1A1C", Unit="%", WindowLabel="Standard / Select / Notion" },
        new() { Id="claude", Name="Claude", Accent="#A3512F", Unit="%", WindowLabel="5時間枠 / 週次枠" },
        new() { Id="chatgpt", Name="ChatGPT / Codex", Accent="#1A1A1C", Unit="%", WindowLabel="モデル別 / 5時間・週次" },
        new() { Id="cursor", Name="Cursor", Accent="#1A1A1C", Unit="USD", WindowLabel="月次の含有使用量" },
        new() { Id="grok", Name="Grok Bot (Cursor)", Accent="#1A1A1C", Unit="%", WindowLabel="Grok Bot週次枠" },
        new() { Id="gemini", Name="Gemini", Accent="#3451B2", Unit="%", WindowLabel="5時間枠 / 週次枠" },
        new() { Id="opencode", Name="OpenCode Go", Accent="#1A1A1C", Unit="%", WindowLabel="5時間 / 週次 / 月次", IsVisible=true },
        new() { Id="copilot", Name="GitHub Copilot", Accent="#1A1A1C", Unit="%", WindowLabel="Premium / Chat", IsVisible=true },
        new() { Id="kimi", Name="Kimi Code", Accent="#1A1A1C", Unit="%", WindowLabel="5時間 / 週次 / 月次", IsVisible=true },
        new() { Id="zai", Name="Z.ai GLM", Accent="#1A1A1C", Unit="%", WindowLabel="5時間 / 週次 / MCP", IsVisible=true },
        new() { Id="openrouter", Name="OpenRouter", Accent="#64748B", Unit="%", WindowLabel="クレジット", IsVisible=false },
        new() { Id="deepseek", Name="DeepSeek", Accent="#5786FE", Unit="USD", WindowLabel="残高", IsVisible=false }
    ];
}
