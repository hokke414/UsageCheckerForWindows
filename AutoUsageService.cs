using Microsoft.Data.Sqlite;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AIUsageChecker;

public sealed record ProviderAutoResult(
    string ProviderId,
    bool Success,
    decimal? Used,
    decimal? Limit,
    DateTimeOffset? ResetAt,
    string WindowLabel,
    string Detail,
    string Status = "自動",
    string Unit = "%",
    bool IsBalanceOnly = false);

public sealed partial class AutoUsageService
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public async Task<IReadOnlyList<ProviderAutoResult>> RefreshAllAsync(IEnumerable<string>? enabledProviderIds = null, CancellationToken cancellationToken = default)
    {
        var enabled = enabledProviderIds is null ? null : enabledProviderIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool IsEnabled(string id) => enabled is null || enabled.Contains(id);
        var tasks = new List<Task<IReadOnlyList<ProviderAutoResult>>>();
        if (IsEnabled("chatgpt")) tasks.Add(FetchCodexAsync(cancellationToken));
        if (IsEnabled("claude")) tasks.Add(FetchClaudeAsync(cancellationToken));
        if (IsEnabled("cursor") || IsEnabled("grok")) tasks.Add(FetchCursorAndGrokAsync(cancellationToken));
        if (IsEnabled("notion")) tasks.Add(FetchNotionAsync(cancellationToken));
        if (IsEnabled("gemini")) tasks.Add(FetchGeminiCliAsync(cancellationToken));
        if (IsEnabled("opencode")) tasks.Add(FetchOpenCodeGoAsync(cancellationToken));
        if (IsEnabled("copilot")) tasks.Add(FetchCopilotAsync(cancellationToken));
        if (IsEnabled("kimi")) tasks.Add(FetchKimiAsync(cancellationToken));
        if (IsEnabled("zai")) tasks.Add(FetchZaiAsync(cancellationToken));
        if (IsEnabled("openrouter")) tasks.Add(FetchOpenRouterAsync(cancellationToken));
        if (IsEnabled("deepseek")) tasks.Add(FetchDeepSeekAsync(cancellationToken));
        var groups = await Task.WhenAll(tasks);
        return groups.SelectMany(item => item).Where(item => IsEnabled(item.ProviderId)).ToList();
    }

    private async Task<IReadOnlyList<ProviderAutoResult>> FetchNotionAsync(CancellationToken cancellationToken)
    {
        try
        {
            var store = new ConnectionSettingsStore();
            var token = store.LoadNotionToken();
            if (string.IsNullOrWhiteSpace(token))
                return [Missing("notion", "設定からNotionの接続情報を登録してください。")];
            var settings = await store.LoadAsync();
            var workspaceId = NormalizeWorkspaceId(settings.NotionWorkspaceId);
            string? workspaceName = null;

            if (string.IsNullOrWhiteSpace(workspaceId))
            {
                using var spacesRequest = NotionRequest("https://app.notion.com/api/v3/getSpaces", token, "{}");
                using var spacesResponse = await _http.SendAsync(spacesRequest, cancellationToken);
                if (spacesResponse.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                    return [Missing("notion", "Notion接続の有効期限が切れています。設定から接続情報を更新してください。")];
                if (!spacesResponse.IsSuccessStatusCode)
                    return [Failed("notion", $"Notion getSpaces: HTTP {(int)spacesResponse.StatusCode}")];
                using var spaces = JsonDocument.Parse(await spacesResponse.Content.ReadAsStreamAsync(cancellationToken));
                var workspace = FindNotionWorkspaces(spaces.RootElement)
                    .OrderByDescending(item => IsPaidNotionPlan(item.Plan))
                    .FirstOrDefault();
                workspaceId = workspace?.Id;
                workspaceName = workspace?.Name;
            }

            if (string.IsNullOrWhiteSpace(workspaceId))
                return [Missing("notion", "Notionワークスペースを特定できません。設定にWorkspace IDを入力してください。")];

            var body = JsonSerializer.Serialize(new { spaceId = workspaceId });
            using var usageRequest = NotionRequest("https://app.notion.com/api/v3/getCreditRateLimitStatus", token, body);
            using var usageResponse = await _http.SendAsync(usageRequest, cancellationToken);
            var windows = new List<NotionWindow>();
            string? rateError = null;
            var rateUnauthorized = false;
            var rateNotApplicable = false;

            if (usageResponse.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                rateUnauthorized = true;
            }
            else if (!usageResponse.IsSuccessStatusCode)
            {
                rateError = $"Notion usage API: HTTP {(int)usageResponse.StatusCode}";
            }
            else
            {
                using var document = JsonDocument.Parse(await usageResponse.Content.ReadAsStreamAsync(cancellationToken));
                var root = document.RootElement;
                var status = StringProperty(root, "status");
                if (string.Equals(status, "not_applicable", StringComparison.OrdinalIgnoreCase))
                {
                    rateNotApplicable = true;
                }
                else
                {
                    // November 2026 Standard / Select credits arrive as resolved allowances on the
                    // same endpoint (`bucketId` auto = Standard, flex = Select). Keep the legacy
                    // Rolling / Monthly windows when no usable allowance is present.
                    AddNotionAllowanceWindows(root, windows);
                    if (windows.Count == 0) AddNotionRateWindows(root, windows);
                }
            }

            // Notion credits (and legacy credit groups) live on the eligibility endpoint. Treat it
            // as optional so workspaces that only expose the allowance response keep working.
            var eligibility = await FetchNotionEligibilityAsync(token, workspaceId, cancellationToken);
            if (windows.Count == 0) windows.AddRange(eligibility.Windows);
            windows = windows
                .GroupBy(item => item.Label, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.MaxBy(item => item.Percent)!)
                .ToList();

            if (windows.Count == 0)
            {
                if (rateNotApplicable)
                    return [new("notion", false, null, null, null, "", "Notion AI利用枠はBusiness / Enterpriseワークスペースで利用できます。", "対象外")];
                if (eligibility.Balance is decimal balance)
                    return
                    [
                        new("notion", true, balance, null, null, "Notionクレジット残高",
                            BuildNotionDetail([], eligibility, workspaceName), "自動", "クレジット", true)
                    ];
                if (rateUnauthorized)
                    return [Missing("notion", "Notion接続の有効期限が切れています。設定から接続情報を更新してください。")];
                return [Failed("notion", rateError ?? "Notionの利用枠レスポンスを判定できませんでした。")];
            }

            var tightest = windows.MaxBy(item => item.Percent)!;
            return [new("notion", true, tightest.Percent, 100, tightest.Reset, tightest.Label,
                BuildNotionDetail(windows, eligibility, workspaceName))];
        }
        catch (Exception ex) { return [Failed("notion", SafeMessage("Notion", ex))]; }
    }

    private async Task<NotionEligibility> FetchNotionEligibilityAsync(string token, string workspaceId, CancellationToken cancellationToken)
    {
        try
        {
            var body = JsonSerializer.Serialize(new { spaceId = workspaceId });
            using var request = NotionRequest("https://app.notion.com/api/v3/getAIUsageEligibilityV2", token, body);
            using var response = await _http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode) return new([], null, null);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
            return ParseNotionEligibility(document.RootElement);
        }
        catch
        {
            return new([], null, null);
        }
    }

    private static NotionEligibility ParseNotionEligibility(JsonElement root)
    {
        var windows = new List<NotionWindow>();
        AddNotionCreditGroup(root, "standardCredits", "standard_credits", "Standard", windows);
        AddNotionCreditGroup(root, "selectCredits", "select_credits", "Select", windows);
        // Older eligibility payloads use basic/premium credit groups. Only accept them when the
        // numbers look like a current allowance instead of lifetime usage.
        AddNotionPlausibleCredit(root, "basicCredits", "basic_credits", "Standard", windows);
        AddNotionPlausibleCredit(root, "premiumCredits", "premium_credits", "Select", windows);

        decimal? balance = null;
        if (TryProperty(root, out var premium, "premiumCredits", "premium_credits"))
            balance = DecimalProperty(premium, "totalCreditBalance", "total_credit_balance");
        if (balance is null && TryProperty(root, out var usage, "usage"))
            balance = DecimalProperty(usage, "totalCreditBalance", "total_credit_balance");
        if (balance is null)
            AddNotionBalanceGroup(root, "notionCredits", "notion_credits", out balance);

        string? detail = null;
        if (TryProperty(root, out var detailSource, "premiumCredits", "premium_credits"))
        {
            var overage = DecimalProperty(detailSource, "creditsInOverage", "credits_in_overage");
            if (overage is > 0) detail = $"Notionクレジット超過 {overage:0.##}";
        }
        return new(windows, detail, balance);
    }

    private static void AddNotionCreditGroup(JsonElement root, string camelName, string snakeName, string label, List<NotionWindow> target)
    {
        if (!TryProperty(root, out var group, camelName, snakeName) || group.ValueKind != JsonValueKind.Object) return;
        var used = DecimalProperty(group, "userUsage", "user_usage", "used", "usage");
        var limit = DecimalProperty(group, "userLimit", "user_limit", "limit", "total");
        if (used is null || limit is not > 0)
        {
            used = DecimalProperty(group, "spaceUsage", "space_usage");
            limit = DecimalProperty(group, "spaceLimit", "space_limit");
        }
        if ((used is null || limit is not > 0) &&
            TryProperty(group, out var perSource, "perSource", "per_source") &&
            TryProperty(perSource, out var monthly, "monthlyAllocated", "monthly_allocated"))
        {
            used = DecimalProperty(monthly, "usageTotal", "usage_total");
            limit = DecimalProperty(monthly, "limit", "totalLimit");
        }
        if (used is null || limit is not > 0) return;
        var reset = DateProperty(group, "periodEndMs", "period_end_ms", "servicePeriodEndMs",
            "service_period_end_ms", "nextResetTime", "resetsAt", "reset_at");
        target.Add(new(label, used.Value / limit.Value * 100, reset));
    }

    private static void AddNotionPlausibleCredit(JsonElement root, string camelName, string snakeName, string label, List<NotionWindow> target)
    {
        if (!TryProperty(root, out var group, camelName, snakeName) || group.ValueKind != JsonValueKind.Object) return;
        var candidates = new List<(decimal Used, decimal Limit)>();
        AddNotionCandidate(candidates, group, "userUsage", "user_usage", "userLimit", "user_limit");
        AddNotionCandidate(candidates, group, "spaceUsage", "space_usage", "spaceLimit", "space_limit");
        AddNotionCandidate(candidates, group, "used", "usage", "limit", "total");
        if (TryProperty(group, out var perSource, "perSource", "per_source") &&
            TryProperty(perSource, out var monthly, "monthlyAllocated", "monthly_allocated"))
            AddNotionCandidate(candidates, monthly, "usageTotal", "usage_total", "limit", "limit");

        var chosen = candidates.FirstOrDefault(item => item.Limit > 0 && item.Used >= 0 && item.Used / item.Limit <= 1.5m);
        if (chosen.Limit <= 0) return;
        var reset = DateProperty(group, "periodEndMs", "period_end_ms", "servicePeriodEndMs",
            "service_period_end_ms", "nextResetTime", "resetsAt", "reset_at");
        target.Add(new(label, chosen.Used / chosen.Limit * 100, reset));
    }

    private static void AddNotionCandidate(List<(decimal Used, decimal Limit)> target, JsonElement element,
        string usedCamel, string usedSnake, string limitCamel, string limitSnake)
    {
        var used = DecimalProperty(element, usedCamel, usedSnake);
        var limit = DecimalProperty(element, limitCamel, limitSnake);
        if (used.HasValue && limit is > 0) target.Add((used.Value, limit.Value));
    }

    private static void AddNotionBalanceGroup(JsonElement root, string camelName, string snakeName, out decimal? balance)
    {
        balance = null;
        if (!TryProperty(root, out var group, camelName, snakeName) || group.ValueKind != JsonValueKind.Object) return;
        balance = DecimalProperty(group, "totalCreditBalance", "total_credit_balance", "balance", "available", "remaining");
    }

    private static string BuildNotionDetail(IEnumerable<NotionWindow> windows, NotionEligibility eligibility, string? workspaceName)
    {
        var parts = windows.Select(item => $"{item.Label} {item.Percent:0.#}%").ToList();
        if (eligibility.Balance is decimal balance) parts.Add($"Notionクレジット残り {balance:0.##}");
        if (!string.IsNullOrWhiteSpace(eligibility.Detail)) parts.Add(eligibility.Detail);
        if (!string.IsNullOrWhiteSpace(workspaceName)) parts.Add(workspaceName);
        return string.Join(" / ", parts);
    }

    private static void AddNotionAllowanceWindows(JsonElement root, List<NotionWindow> target)
    {
        JsonElement allowances;
        if (TryProperty(root, out var direct, "allowances"))
        {
            allowances = direct;
        }
        else if (TryProperty(root, out var data, "data") && data.ValueKind == JsonValueKind.Object &&
                 TryProperty(data, out var nested, "allowances"))
        {
            allowances = nested;
        }
        else
        {
            return;
        }

        var items = new List<JsonElement>();
        if (allowances.ValueKind == JsonValueKind.Array) items.AddRange(allowances.EnumerateArray());
        else if (allowances.ValueKind == JsonValueKind.Object)
            foreach (var property in allowances.EnumerateObject()) items.Add(property.Value);

        foreach (var item in items)
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var label = NotionAllowanceLabel(StringProperty(item, "bucketId", "bucket_id", "bucket", "id", "type"));
            if (label is null) continue;

            // Notion asks clients to keep the legacy windows while a bucket is not resolved.
            var state = StringProperty(item, "state", "status");
            if (!string.IsNullOrWhiteSpace(state) && !state.Equals("resolved", StringComparison.OrdinalIgnoreCase)) continue;

            var source = TryProperty(item, out var usage, "usage", "detail") && usage.ValueKind == JsonValueKind.Object
                ? usage
                : item;
            var used = DecimalProperty(source, "used", "usage", "usedCredits", "creditsUsed", "currentUsage", "userUsage", "spaceUsage", "consumed");
            var limit = DecimalProperty(source, "limit", "totalLimit", "total_limit", "creditsLimit", "entitlement", "total", "allowance");
            if (used is null)
                used = DecimalProperty(item, "used", "usage", "usedCredits", "creditsUsed", "currentUsage", "userUsage", "spaceUsage", "consumed");
            if (limit is null)
                limit = DecimalProperty(item, "limit", "totalLimit", "total_limit", "creditsLimit", "entitlement", "total", "allowance");

            var percent = DecimalProperty(source, "percent", "usedPercent", "usagePercent")
                ?? DecimalProperty(item, "percent", "usedPercent", "usagePercent");
            if (percent is not null)
            {
                used = percent;
                limit = 100;
            }
            if (used is null && limit is > 0)
            {
                var remaining = DecimalProperty(source, "remaining", "remainingCredits", "balance")
                    ?? DecimalProperty(item, "remaining", "remainingCredits", "balance");
                if (remaining.HasValue) used = limit - remaining.Value;
            }
            if (used is null || limit is not > 0) continue;

            var reset = DateProperty(item, "periodEndMs", "period_end_ms", "servicePeriodEndMs", "service_period_end_ms",
                "resetsAt", "resets_at", "reset_at", "resetTime", "nextResetTime")
                ?? DateProperty(source, "periodEndMs", "period_end_ms", "servicePeriodEndMs", "service_period_end_ms",
                    "resetsAt", "resets_at", "reset_at", "resetTime", "nextResetTime");
            target.Add(new(label, used.Value / limit.Value * 100, reset));
        }
    }

    private static string? NotionAllowanceLabel(string? bucket) => bucket?.Trim().ToLowerInvariant() switch
    {
        "auto" => "Standard",
        "flex" => "Select",
        _ => null
    };

    private static void AddNotionRateWindows(JsonElement root, List<NotionWindow> target)
    {
        var resetSeconds = DecimalProperty(root, "resetsInSeconds", "resets_in_seconds");
        AddNotionWindowAt(root, "window", target, resetSeconds);
        AddNotionWindowAt(root, "billingPeriodWindow", target, null);
        AddNotionWindowAt(root, "standardWindow", target, resetSeconds);
        AddNotionWindowAt(root, "selectWindow", target, resetSeconds);
        AddNotionWindowAt(root, "standardBillingPeriodWindow", target, null);
        AddNotionWindowAt(root, "selectBillingPeriodWindow", target, null);

        foreach (var property in root.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Object) continue;
            if (property.Name is "window" or "billingPeriodWindow" or "standardWindow" or "selectWindow"
                or "standardBillingPeriodWindow" or "selectBillingPeriodWindow") continue;
            if (property.Name is "limits" or "dependencies" or "usage" or "perSource") continue;
            if (LooksLikeNotionWindow(property.Value))
                TryAddNotionWindow(property.Value, property.Name, target, null);
        }
    }

    private static void AddNotionWindowAt(JsonElement parent, string name, List<NotionWindow> target, decimal? resetSeconds)
    {
        if (!TryProperty(parent, out var window, name) || window.ValueKind != JsonValueKind.Object) return;
        TryAddNotionWindow(window, name, target, resetSeconds);
    }

    private static bool LooksLikeNotionWindow(JsonElement element) =>
        DecimalProperty(element, "used", "usage", "currentValue", "userUsage", "spaceUsage").HasValue &&
        DecimalProperty(element, "limit", "entitlement", "total", "userLimit", "spaceLimit").HasValue;

    private static void TryAddNotionWindow(JsonElement element, string propertyName, List<NotionWindow> target, decimal? resetSeconds)
    {
        if (!TryReadNotionWindow(element, propertyName, out var window)) return;
        if (window.Reset is null && resetSeconds.HasValue && propertyName.Contains("window", StringComparison.OrdinalIgnoreCase))
            window = window with { Reset = DateTimeOffset.Now.AddSeconds((double)resetSeconds.Value) };
        target.Add(window);
    }

    private static bool TryReadNotionWindow(JsonElement element, string propertyName, out NotionWindow window)
    {
        window = default!;
        if (element.ValueKind != JsonValueKind.Object) return false;

        var used = DecimalProperty(element, "used", "usage", "currentValue", "userUsage", "spaceUsage");
        var limit = DecimalProperty(element, "limit", "entitlement", "total", "userLimit", "spaceLimit", "totalLimit");
        var remaining = DecimalProperty(element, "remaining", "remainingCount", "remaining_credits");
        if (used is null && limit is > 0 && remaining.HasValue) used = limit - remaining;
        if (used is null || limit is not > 0) return false;

        var creditType = StringProperty(element, "creditType", "credit_type", "type");
        var cadence = StringProperty(element, "cadence", "window");
        var label = NotionWindowLabel(propertyName, creditType, cadence);
        var reset = DateProperty(element, "periodEndMs", "period_end_ms", "servicePeriodEndMs", "service_period_end_ms",
            "resetsAt", "resets_at", "reset_at", "nextResetTime", "resetTime");
        window = new(label, used.Value / limit.Value * 100, reset);
        return true;
    }

    private static string NotionWindowLabel(string propertyName, string? creditType, string? cadence)
    {
        var credit = NotionCreditLabel(creditType);
        if (string.IsNullOrWhiteSpace(credit))
        {
            if (propertyName.Contains("standard", StringComparison.OrdinalIgnoreCase)) credit = "Standard";
            else if (propertyName.Contains("select", StringComparison.OrdinalIgnoreCase)) credit = "Select";
            else if (propertyName.Contains("notion", StringComparison.OrdinalIgnoreCase)) credit = "Notion";
        }

        string? period = null;
        var source = $"{propertyName} {cadence}".ToLowerInvariant();
        if (source.Contains("billing") || source.Contains("month")) period = "月次";
        else if (source.Contains("6h") || source.Contains("6時間") || propertyName.Equals("window", StringComparison.OrdinalIgnoreCase)) period = "6時間";
        else if (source.Contains("week")) period = "週次";
        else if (source.Contains("day")) period = "日次";

        if (!string.IsNullOrWhiteSpace(credit) && period is not null) return $"{credit} {period}";
        return credit ?? period ?? "利用枠";
    }

    private static string? NotionCreditLabel(string? creditType)
    {
        if (string.IsNullOrWhiteSpace(creditType)) return null;
        if (creditType.Contains("standard", StringComparison.OrdinalIgnoreCase)) return "Standard";
        if (creditType.Contains("select", StringComparison.OrdinalIgnoreCase)) return "Select";
        if (creditType.Contains("notion", StringComparison.OrdinalIgnoreCase) ||
            creditType.Contains("premium", StringComparison.OrdinalIgnoreCase)) return "Notion";
        return null;
    }

    private sealed record NotionWindow(string Label, decimal Percent, DateTimeOffset? Reset);
    private sealed record NotionEligibility(List<NotionWindow> Windows, string? Detail, decimal? Balance);

    public async Task<IReadOnlyList<ProviderAutoResult>> FetchGeminiCliAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var path = GeminiCredentialPath();
            if (path is null) return [Missing("gemini", "設定からGemini CLIを起動し、Googleアカウントでログインしてください。")];
            using var auth = JsonDocument.Parse(await File.ReadAllTextAsync(path, cancellationToken));
            var accessToken = StringProperty(auth.RootElement, "access_token", "accessToken");
            var refreshToken = StringProperty(auth.RootElement, "refresh_token", "refreshToken");
            if (string.IsNullOrWhiteSpace(accessToken)) return [Missing("gemini", "Gemini CLIのOAuthトークンを確認できません。再ログインしてください。")];
            var expiry = DecimalProperty(auth.RootElement, "expiry_date", "expiryDate");
            if (expiry.HasValue && DateTimeOffset.FromUnixTimeMilliseconds((long)expiry.Value) <= DateTimeOffset.Now.AddMinutes(1))
            {
                accessToken = await RefreshGeminiTokenAsync(path, refreshToken, auth.RootElement, cancellationToken);
                if (string.IsNullOrWhiteSpace(accessToken))
                    return [Missing("gemini", "Gemini CLIのログイン期限が切れています。設定からCLIを起動して再ログインしてください。")];
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, "https://cloudcode-pa.googleapis.com/v1internal:retrieveUserQuota");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Headers.UserAgent.ParseAdd("gemini-cli");
            request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
            using var response = await _http.SendAsync(request, cancellationToken);
            if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
                return [Missing("gemini", "Gemini CLI認証でクォータを取得できません。再ログインするか、個人向けアカウントではAntigravity連携が必要な場合があります。")];
            if (!response.IsSuccessStatusCode) return [Failed("gemini", $"Gemini quota API: HTTP {(int)response.StatusCode}")];

            using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
            var buckets = new List<(string Model, decimal Used, DateTimeOffset? Reset)>();
            CollectGeminiBuckets(document.RootElement, buckets);
            if (buckets.Count == 0) return [Failed("gemini", "Geminiのクォータレスポンスに利用枠がありませんでした。")];
            var tightest = buckets.MaxBy(item => item.Used);
            var detail = string.Join(" / ", buckets.OrderByDescending(item => item.Used).Take(3).Select(item => $"{ShortModel(item.Model)} {item.Used:0.#}%"));
            return [new("gemini", true, tightest.Used, 100, tightest.Reset, ShortModel(tightest.Model), detail)];
        }
        catch (Exception ex) { return [Failed("gemini", SafeMessage("Gemini", ex))]; }
    }

    private async Task<string?> RefreshGeminiTokenAsync(string credentialPath, string? refreshToken, JsonElement auth, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(refreshToken)) return null;
        var clientId = StringProperty(auth, "client_id", "clientId") ?? Environment.GetEnvironmentVariable("GEMINI_OAUTH_CLIENT_ID");
        var clientSecret = StringProperty(auth, "client_secret", "clientSecret") ?? Environment.GetEnvironmentVariable("GEMINI_OAUTH_CLIENT_SECRET");
        if (string.IsNullOrWhiteSpace(clientId))
        {
            var oauthSource = FindGeminiOauthSource(credentialPath);
            if (oauthSource is not null)
            {
                var source = await File.ReadAllTextAsync(oauthSource, cancellationToken);
                clientId = RegexValue(source, @"OAUTH_CLIENT_ID\s*=\s*['\""`]([^'\""`]+)");
                clientSecret = RegexValue(source, @"OAUTH_CLIENT_SECRET\s*=\s*['\""`]([^'\""`]+)");
            }
        }
        if (string.IsNullOrWhiteSpace(clientId)) return null;

        var form = new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["refresh_token"] = refreshToken,
            ["grant_type"] = "refresh_token"
        };
        if (!string.IsNullOrWhiteSpace(clientSecret)) form["client_secret"] = clientSecret;
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://oauth2.googleapis.com/token") { Content = new FormUrlEncodedContent(form) };
        using var response = await _http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode) return null;
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
        return StringProperty(document.RootElement, "access_token");
    }

    private async Task<IReadOnlyList<ProviderAutoResult>> FetchCodexAsync(CancellationToken cancellationToken)
    {
        try
        {
            var home = Environment.GetEnvironmentVariable("CODEX_HOME");
            if (string.IsNullOrWhiteSpace(home))
                home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
            var authPath = Path.Combine(home, "auth.json");
            if (!File.Exists(authPath)) return [Missing("chatgpt", "Codexのログイン情報が見つかりません。Codexへログインしてください。")];

            using var auth = JsonDocument.Parse(await File.ReadAllTextAsync(authPath, cancellationToken));
            var tokens = auth.RootElement.GetProperty("tokens");
            var accessToken = tokens.GetProperty("access_token").GetString();
            var accountId = tokens.TryGetProperty("account_id", out var account) ? account.GetString() : null;
            if (string.IsNullOrWhiteSpace(accessToken)) return [Missing("chatgpt", "Codexのアクセストークンが空です。再ログインしてください。")];

            using var request = new HttpRequestMessage(HttpMethod.Get, "https://chatgpt.com/backend-api/wham/usage");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Headers.UserAgent.ParseAdd("codex-cli");
            if (!string.IsNullOrWhiteSpace(accountId)) request.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", accountId);
            using var response = await _http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode) return [Failed("chatgpt", $"Codex usage API: HTTP {(int)response.StatusCode}")];

            using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
            var root = document.RootElement;
            var windows = new List<(string Label, decimal Used, DateTimeOffset? Reset)>();
            if (TryProperty(root, out var rateLimit, "rate_limit", "rateLimit"))
            {
                AddCodexWindow(rateLimit, windows, "primary_window", "primaryWindow");
                AddCodexWindow(rateLimit, windows, "secondary_window", "secondaryWindow");
            }
            if (windows.Count == 0) return [Failed("chatgpt", "Codex usage APIに利用枠が含まれていませんでした。")];

            var tightest = windows.MaxBy(item => item.Used);
            var plan = TryProperty(root, out var planValue, "plan_type", "planType") ? planValue.GetString() : null;
            var detail = string.Join(" / ", windows.Select(item => $"{item.Label} {item.Used:0.#}%"));
            if (!string.IsNullOrWhiteSpace(plan)) detail += $" · {plan}";
            return [new("chatgpt", true, tightest.Used, 100, tightest.Reset, tightest.Label, detail)];
        }
        catch (Exception ex)
        {
            return [Failed("chatgpt", SafeMessage("Codex", ex))];
        }
    }

    private async Task<IReadOnlyList<ProviderAutoResult>> FetchClaudeAsync(CancellationToken cancellationToken)
    {
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", ".credentials.json");
            if (!File.Exists(path)) return [Missing("claude", "Claude Codeのログイン情報が見つかりません。Claude Codeへログインしてください。")];
            using var auth = JsonDocument.Parse(await File.ReadAllTextAsync(path, cancellationToken));
            if (!TryProperty(auth.RootElement, out var oauth, "claudeAiOauth") ||
                !TryProperty(oauth, out var tokenElement, "accessToken", "access_token") ||
                string.IsNullOrWhiteSpace(tokenElement.GetString()))
                return [Missing("claude", "Claude CodeのOAuthログイン情報を読み取れませんでした。")];

            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/api/oauth/usage");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenElement.GetString());
            request.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
            using var response = await _http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode) return [Failed("claude", $"Claude usage API: HTTP {(int)response.StatusCode}")];

            using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
            var windows = new List<(string Label, decimal Used, DateTimeOffset? Reset)>();
            AddClaudeWindow(document.RootElement, windows, "five_hour", "5時間");
            AddClaudeWindow(document.RootElement, windows, "seven_day", "週次");
            AddClaudeWindow(document.RootElement, windows, "seven_day_opus", "Opus週次");
            AddClaudeWindow(document.RootElement, windows, "seven_day_sonnet", "Sonnet週次");
            if (windows.Count == 0) return [Failed("claude", "Claude usage APIに利用枠が含まれていませんでした。")];
            var tightest = windows.MaxBy(item => item.Used);
            return [new("claude", true, tightest.Used, 100, tightest.Reset, tightest.Label,
                string.Join(" / ", windows.Select(item => $"{item.Label} {item.Used:0.#}%")))];
        }
        catch (Exception ex)
        {
            return [Failed("claude", SafeMessage("Claude", ex))];
        }
    }

    private async Task<IReadOnlyList<ProviderAutoResult>> FetchCursorAndGrokAsync(CancellationToken cancellationToken)
    {
        var results = new List<ProviderAutoResult>();
        try
        {
            var databasePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Cursor", "User", "globalStorage", "state.vscdb");
            if (!File.Exists(databasePath))
                return [Missing("cursor", "Cursorのローカルセッションが見つかりません。"), Missing("grok", "Cursorログインが必要です。")];

            var token = await ReadCursorTokenAsync(databasePath, cancellationToken);
            if (string.IsNullOrWhiteSpace(token))
                return [Missing("cursor", "Cursorへログインすると自動取得できます。"), Missing("grok", "CursorへログインするとGrok Bot使用量を取得できます。")];
            var userId = ReadJwtSubject(token);
            if (string.IsNullOrWhiteSpace(userId))
                return [Failed("cursor", "CursorセッションのユーザーIDを確認できません。"), Failed("grok", "Cursorセッションを確認できません。")];
            var cookie = $"WorkosCursorSessionToken={Uri.EscapeDataString($"{userId}::{token}")}";

            using (var request = new HttpRequestMessage(HttpMethod.Get, "https://cursor.com/api/usage-summary"))
            {
                request.Headers.TryAddWithoutValidation("Cookie", cookie);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                using var response = await _http.SendAsync(request, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    results.Add(Failed("cursor", $"Cursor usage API: HTTP {(int)response.StatusCode}"));
                }
                else
                {
                    using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
                    results.Add(ParseCursor(document.RootElement));
                }
            }

            using (var request = new HttpRequestMessage(HttpMethod.Post, "https://cursor.com/api/dashboard/get-sand-usage-status"))
            {
                request.Headers.TryAddWithoutValidation("Cookie", cookie);
                request.Headers.TryAddWithoutValidation("Origin", "https://cursor.com");
                request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
                using var response = await _http.SendAsync(request, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    results.Add(Failed("grok", $"Grok Bot usage API: HTTP {(int)response.StatusCode}"));
                }
                else
                {
                    using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
                    results.Add(ParseGrok(document.RootElement));
                }
            }
            return results;
        }
        catch (Exception ex)
        {
            if (results.All(item => item.ProviderId != "cursor")) results.Add(Failed("cursor", SafeMessage("Cursor", ex)));
            if (results.All(item => item.ProviderId != "grok")) results.Add(Failed("grok", SafeMessage("Grok Bot", ex)));
            return results;
        }
    }

    private static ProviderAutoResult ParseCursor(JsonElement root)
    {
        decimal? percent = null;
        decimal? used = null;
        decimal? limit = null;
        if (TryProperty(root, out var individual, "individualUsage") && TryProperty(individual, out var plan, "plan"))
        {
            percent = DecimalProperty(plan, "totalPercentUsed");
            used = DecimalProperty(plan, "used");
            limit = DecimalProperty(plan, "limit");
        }
        if (percent is null && TryProperty(root, out individual, "individualUsage") && TryProperty(individual, out var overall, "overall"))
        {
            used = DecimalProperty(overall, "used");
            limit = DecimalProperty(overall, "limit");
        }
        if (percent is null && used.HasValue && limit is > 0) percent = used.Value / limit.Value * 100;
        if (percent is null) return Failed("cursor", "Cursorの使用量レスポンス形式を判定できませんでした。");
        var reset = DateProperty(root, "billingCycleEnd");
        var planName = StringProperty(root, "membershipType");
        var detail = used.HasValue && limit is > 0
            ? $"月次 {percent:0.#}% · {used:0.##}/{limit:0.##} · {planName}"
            : $"月次 {percent:0.#}% · {planName}";
        return new("cursor", true, percent, 100, reset, "月次枠", detail);
    }

    private static ProviderAutoResult ParseGrok(JsonElement root)
    {
        var percent = DecimalProperty(root, "usagePercent");
        if (percent is null) return Missing("grok", "このCursorアカウントにはGrok Bot利用枠がありません。") with { Status = "対象外" };
        var reset = DateProperty(root, "nextResetTimestampUtc") ?? DateProperty(root, "trialEndTimestampUtc");
        return new("grok", true, percent, 100, reset, "Grok Bot週次枠", $"Grok Bot {percent:0.#}%");
    }

    private static async Task<string?> ReadCursorTokenAsync(string databasePath, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly");
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM ItemTable WHERE key = 'cursorAuth/accessToken' LIMIT 1";
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value switch
        {
            string text => NormalizeToken(text),
            byte[] bytes => NormalizeToken(DecodeBytes(bytes)),
            _ => null
        };
    }

    private static string DecodeBytes(byte[] bytes)
    {
        var looksUtf16 = bytes.Length > 3 && Enumerable.Range(1, Math.Min(bytes.Length, 32) / 2).Count(i => bytes[i * 2 - 1] == 0) > 5;
        return (looksUtf16 ? Encoding.Unicode : Encoding.UTF8).GetString(bytes);
    }

    private static string NormalizeToken(string value)
    {
        value = value.Trim().Trim('\0');
        if (value.StartsWith('"') && value.EndsWith('"'))
        {
            try { return JsonSerializer.Deserialize<string>(value) ?? ""; } catch { }
        }
        return value;
    }

    private static string? ReadJwtSubject(string token)
    {
        try
        {
            var parts = token.Split('.');
            if (parts.Length < 2) return null;
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload += new string('=', (4 - payload.Length % 4) % 4);
            using var document = JsonDocument.Parse(Convert.FromBase64String(payload));
            return StringProperty(document.RootElement, "sub");
        }
        catch { return null; }
    }

    private static void AddCodexWindow(JsonElement parent, List<(string, decimal, DateTimeOffset?)> target, params string[] names)
    {
        if (!TryProperty(parent, out var window, names)) return;
        var used = DecimalProperty(window, "used_percent", "usedPercent");
        if (!used.HasValue) return;
        var seconds = DecimalProperty(window, "limit_window_seconds", "limitWindowSeconds");
        var label = seconds switch
        {
            >= 17000 and <= 19000 => "5時間",
            >= 600000 and <= 610000 => "週次",
            _ => "利用枠"
        };
        target.Add((label, used.Value, UnixDateProperty(window, "reset_at", "resetAt")));
    }

    private static void AddClaudeWindow(JsonElement root, List<(string, decimal, DateTimeOffset?)> target, string key, string label)
    {
        if (!TryProperty(root, out var window, key)) return;
        var used = DecimalProperty(window, "utilization", "used_percent");
        if (!used.HasValue) return;
        if (used <= 1) used *= 100;
        var reset = DateProperty(window, "resets_at") ?? UnixDateProperty(window, "reset_at");
        target.Add((label, used.Value, reset));
    }

    private static bool TryProperty(JsonElement element, out JsonElement value, params string[] names)
    {
        if (element.ValueKind == JsonValueKind.Object)
            foreach (var name in names)
                if (element.TryGetProperty(name, out value)) return true;
        value = default;
        return false;
    }

    private static decimal? DecimalProperty(JsonElement element, params string[] names)
    {
        if (!TryProperty(element, out var value, names)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number)) return number;
        if (value.ValueKind == JsonValueKind.String && decimal.TryParse(value.GetString(), out number)) return number;
        return null;
    }

    private static string? StringProperty(JsonElement element, params string[] names) =>
        TryProperty(element, out var value, names) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static DateTimeOffset? DateProperty(JsonElement element, params string[] names)
    {
        if (!TryProperty(element, out var value, names)) return null;
        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString();
            if (DateTimeOffset.TryParse(text, out var date)) return date;
            if (long.TryParse(text, out var epoch)) return epoch > 10_000_000_000 ? DateTimeOffset.FromUnixTimeMilliseconds(epoch) : DateTimeOffset.FromUnixTimeSeconds(epoch);
        }
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
            return number > 10_000_000_000 ? DateTimeOffset.FromUnixTimeMilliseconds(number) : DateTimeOffset.FromUnixTimeSeconds(number);
        return null;
    }

    private static DateTimeOffset? UnixDateProperty(JsonElement element, params string[] names) => DateProperty(element, names);
    private static HttpRequestMessage NotionRequest(string url, string token, string json)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.TryAddWithoutValidation("Cookie", $"token_v2={token}");
        request.Headers.TryAddWithoutValidation("Origin", "https://app.notion.com");
        request.Headers.Referrer = new Uri("https://app.notion.com/");
        request.Headers.UserAgent.ParseAdd("AIUsageChecker/1.4");
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return request;
    }

    private sealed record NotionWorkspace(string Id, string? Name, string? Plan);

    private static IEnumerable<NotionWorkspace> FindNotionWorkspaces(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var fromNestedSpace = TryProperty(element, out var space, "space") && space.ValueKind == JsonValueKind.Object;
            var source = fromNestedSpace ? space : element;
            var id = StringProperty(source, "id", "space_id", "spaceId");
            var plan = StringProperty(source, "plan_type", "planType", "subscription_tier", "subscriptionTier");
            var name = StringProperty(source, "name");
            if (!string.IsNullOrWhiteSpace(id) && (!string.IsNullOrWhiteSpace(plan) || fromNestedSpace))
                yield return new(NormalizeWorkspaceId(id)!, name, plan);
            foreach (var property in element.EnumerateObject())
                foreach (var candidate in FindNotionWorkspaces(property.Value)) yield return candidate;
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray())
                foreach (var candidate in FindNotionWorkspaces(item)) yield return candidate;
    }

    private static bool IsPaidNotionPlan(string? plan) =>
        plan?.Contains("business", StringComparison.OrdinalIgnoreCase) == true ||
        plan?.Contains("enterprise", StringComparison.OrdinalIgnoreCase) == true;

    private static string? NormalizeWorkspaceId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var compact = value.Replace("-", "").Trim();
        if (compact.Length != 32) return value.Trim();
        return $"{compact[..8]}-{compact[8..12]}-{compact[12..16]}-{compact[16..20]}-{compact[20..]}";
    }

    private static void CollectGeminiBuckets(JsonElement element, List<(string, decimal, DateTimeOffset?)> target)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var remaining = DecimalProperty(element, "remainingFraction", "remaining_fraction");
            if (remaining.HasValue)
            {
                var model = StringProperty(element, "modelId", "model_id", "model") ?? "Gemini";
                var used = Math.Clamp((1 - remaining.Value) * 100, 0, 100);
                target.Add((model, used, DateProperty(element, "resetTime", "reset_time")));
                return;
            }
            foreach (var property in element.EnumerateObject()) CollectGeminiBuckets(property.Value, target);
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) CollectGeminiBuckets(item, target);
    }

    private static string ShortModel(string model)
    {
        var value = model.Replace("models/", "", StringComparison.OrdinalIgnoreCase).Replace("gemini-", "", StringComparison.OrdinalIgnoreCase);
        return value.Length <= 18 ? value : value[..18] + "…";
    }

    private static string? FindGeminiOauthSource(string credentialPath)
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var candidates = new[]
        {
            Path.Combine(appData, "npm", "node_modules", "@google", "gemini-cli", "node_modules", "@google", "gemini-cli-core", "dist", "src", "code_assist", "oauth2.js"),
            Path.Combine(appData, "npm", "node_modules", "@google", "gemini-cli-core", "dist", "src", "code_assist", "oauth2.js"),
            Path.Combine(Path.GetDirectoryName(credentialPath) ?? "", "oauth2.js")
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static string? RegexValue(string source, string pattern)
    {
        var match = Regex.Match(source, pattern, RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static ProviderAutoResult Missing(string id, string detail) => new(id, false, null, null, null, "", detail, "要設定");
    private static ProviderAutoResult Failed(string id, string detail) => new(id, false, null, null, null, "", detail, "取得失敗");
    private static string SafeMessage(string provider, Exception error) => $"{provider}: {error.GetType().Name} - {error.Message}";
    private static string? GeminiCredentialPath()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini", "oauth_creds.json");
        return File.Exists(path) ? path : null;
    }
}
