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
            latest.Used = current.Used;
            latest.Limit = current.Limit;
            latest.ResetAt = current.ResetAt;
            latest.Note = current.Note;
            latest.Unit = string.IsNullOrWhiteSpace(current.Unit) ? latest.Unit : current.Unit;
            latest.WindowLabel = string.IsNullOrWhiteSpace(current.WindowLabel) ? latest.WindowLabel : current.WindowLabel;
            latest.AutoStatus = current.AutoStatus;
            latest.AutoDetail = current.AutoDetail;
            latest.LastAutoChecked = current.LastAutoChecked;
            latest.IsVisible = current.IsVisible;
        }
        return defaults;
    }

    private static List<ProviderUsage> Defaults() =>
    [
        new() { Id="notion", Name="Notion AI", ShortName="N", Subtitle="Agent・画像生成・翻訳", Accent="#333333", Unit="%", WindowLabel="6時間枠 / 月次枠", UsageUrl="https://www.notion.so", HelpUrl="https://www.notion.com/help/manage-your-usage-allowance-for-notion-ai", Guidance="Notionの［設定］→［Notion AI］→［Usage］で6時間枠と月次枠を確認します。" },
        new() { Id="claude", Name="Claude", ShortName="C", Subtitle="Claude.ai・Claude Code", Accent="#A3512F", Unit="%", WindowLabel="5時間枠 / 週次枠", UsageUrl="https://claude.ai/settings/usage", HelpUrl="https://support.anthropic.com/en/articles/9797557-usage-limit-best-practices", Guidance="ClaudeのUsage画面に表示された消費率を記録します。会話とClaude Codeで枠を共有する場合があります。" },
        new() { Id="chatgpt", Name="ChatGPT / Codex", ShortName="O", Subtitle="Chat・Codex・Work", Accent="#006B5B", Unit="%", WindowLabel="モデル別 / 5時間・週次", UsageUrl="https://chatgpt.com/#settings/Usage", HelpUrl="https://help.openai.com/en/articles/12642688", Guidance="ChatGPTの［Settings］→［Usage］に表示された対象枠を記録します。モデルや機能で枠が異なります。" },
        new() { Id="cursor", Name="Cursor", ShortName="Cu", Subtitle="Agent・Auto・Bugbot", Accent="#333333", Unit="USD", WindowLabel="月次の含有使用量", UsageUrl="https://cursor.com/dashboard", HelpUrl="https://docs.cursor.com/account/pricing", Guidance="Cursor DashboardのUsageで使用額とトークン内訳を確認します。Bugbotは別枠です。" },
        new() { Id="grok", Name="Grok Bot (Cursor)", ShortName="G", Subtitle="Cursor Dashboard・Grok Bot", Accent="#333333", Unit="%", WindowLabel="Grok Bot週次枠", UsageUrl="https://cursor.com/dashboard", HelpUrl="https://cursor.com/docs/account/usage", Guidance="CursorのGrok Bot使用率を自動取得します。取得できない場合はCursorへログインし、DashboardのUsageを確認してください。" },
        new() { Id="gemini", Name="Gemini", ShortName="Ge", Subtitle="Gemini Apps・CLI", Accent="#3451B2", Unit="%", WindowLabel="5時間枠 / 週次枠", UsageUrl="https://gemini.google.com/usage", HelpUrl="https://support.google.com/gemini/answer/16275805", Guidance="Gemini AppsのUsage画面を自動取得します。取得できない場合は設定からGoogleアカウントへログインしてください。" }
    ];
}
