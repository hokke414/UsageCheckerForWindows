using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AIUsageChecker;

public sealed partial class AutoUsageService
{
    private static readonly string[] OpenCodeAuthNames = ["opencode-go", "opencode"];
    private static readonly string[] KimiAuthPaths =
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".kimi-code", "credentials", "kimi-code.json"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".kimi", "credentials", "kimi-code.json")
    ];

    private async Task<IReadOnlyList<ProviderAutoResult>> FetchOpenCodeGoAsync(CancellationToken cancellationToken)
    {
        try
        {
            var key = LoadApiKey("opencode", "OPENCODE_GO_API_KEY", "OPENCODE_API_KEY")
                ?? await FindOpenCodeAuthSecretAsync(OpenCodeAuthNames, ["key", "apiKey", "access", "token"], cancellationToken);
            if (string.IsNullOrWhiteSpace(key))
                return [Missing("opencode", "OpenCode GoのAPIキーを設定するか、OpenCodeへログインしてください。")];

            using var request = new HttpRequestMessage(HttpMethod.Get, "https://opencode.ai/zen/go/v1/usage");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await _http.SendAsync(request, cancellationToken);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return [Missing("opencode", "OpenCode Goの認証を確認できません。APIキーを更新してください。")];
            if (!response.IsSuccessStatusCode)
                return [Failed("opencode", $"OpenCode Go usage API: HTTP {(int)response.StatusCode}")];

            using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
            return [ParseOpenCodeGo(document.RootElement)];
        }
        catch (Exception ex) { return [Failed("opencode", SafeMessage("OpenCode Go", ex))]; }
    }

    private static ProviderAutoResult ParseOpenCodeGo(JsonElement root)
    {
        var source = TryProperty(root, out var usage, "usage") && usage.ValueKind == JsonValueKind.Object ? usage : root;
        var windows = new List<(string Label, decimal Percent, DateTimeOffset? Reset)>();
        AddOpenCodeWindow(source, windows, "5時間", "rolling", "rolling5h", "rolling_5h");
        AddOpenCodeWindow(source, windows, "週次", "weekly");
        AddOpenCodeWindow(source, windows, "月次", "monthly");
        if (windows.Count == 0) return Failed("opencode", "OpenCode Goの利用枠レスポンスを判定できませんでした。");

        var tightest = windows.MaxBy(item => item.Percent);
        var detail = string.Join(" / ", windows.Select(item => $"{item.Label} {item.Percent:0.#}%"));
        return new("opencode", true, tightest.Percent, 100, tightest.Reset, tightest.Label, detail);
    }

    private static void AddOpenCodeWindow(JsonElement source, List<(string, decimal, DateTimeOffset?)> target, string label, params string[] names)
    {
        if (!TryProperty(source, out var window, names) || window.ValueKind != JsonValueKind.Object) return;
        var percent = DecimalProperty(window, "percent", "usagePercent", "usedPercent");
        if (percent is null)
        {
            var used = DecimalProperty(window, "used", "usageDollars", "usedDollars");
            var limit = DecimalProperty(window, "limit", "limitDollars");
            if (used.HasValue && limit is > 0) percent = used.Value / limit.Value * 100;
        }
        if (percent is null) return;
        var reset = DateProperty(window, "resetsAt", "resetAt", "resetTime")
            ?? NowPlusSeconds(window, "resetInSec", "resetInSeconds");
        target.Add((label, percent.Value, reset));
    }

    private async Task<IReadOnlyList<ProviderAutoResult>> FetchCopilotAsync(CancellationToken cancellationToken)
    {
        try
        {
            var local = await FindCopilotLocalAuthAsync(cancellationToken);
            var oauthToken = local?.Refresh
                ?? EnvironmentValue("COPILOT_GITHUB_TOKEN", "GH_TOKEN", "GITHUB_TOKEN")
                ?? local?.Access;
            var directToken = local?.Access;
            if (string.IsNullOrWhiteSpace(oauthToken) && string.IsNullOrWhiteSpace(directToken))
                return [Missing("copilot", "GitHub Copilotへログインするか、OpenCodeのGitHub Copilot認証を用意してください。")];

            if (!string.IsNullOrWhiteSpace(oauthToken))
            {
                using var tokenRequest = new HttpRequestMessage(HttpMethod.Post, "https://api.github.com/copilot_internal/v2/token");
                tokenRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", oauthToken);
                AddCopilotHeaders(tokenRequest);
                using var tokenResponse = await _http.SendAsync(tokenRequest, cancellationToken);
                if (tokenResponse.IsSuccessStatusCode)
                {
                    using var tokenDocument = JsonDocument.Parse(await tokenResponse.Content.ReadAsStreamAsync(cancellationToken));
                    directToken = StringProperty(tokenDocument.RootElement, "token", "access_token", "accessToken");
                    if (HasCopilotSnapshots(tokenDocument.RootElement))
                        return [ParseCopilot(tokenDocument.RootElement)];
                }
            }

            if (string.IsNullOrWhiteSpace(directToken))
                return [Missing("copilot", "GitHub Copilotのトークンを更新できません。再度ログインしてください。")];

            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/copilot_internal/user");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", directToken);
            AddCopilotHeaders(request);
            using var response = await _http.SendAsync(request, cancellationToken);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return [Missing("copilot", "GitHub Copilotの認証が切れています。再度ログインしてください。")];
            if (!response.IsSuccessStatusCode)
                return [Failed("copilot", $"GitHub Copilot API: HTTP {(int)response.StatusCode}")];

            using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
            return [ParseCopilot(document.RootElement)];
        }
        catch (Exception ex) { return [Failed("copilot", SafeMessage("GitHub Copilot", ex))]; }
    }

    private static void AddCopilotHeaders(HttpRequestMessage request)
    {
        request.Headers.UserAgent.ParseAdd("GitHubCopilotChat/0.26.7");
        request.Headers.TryAddWithoutValidation("Editor-Version", "vscode/1.99.3");
        request.Headers.TryAddWithoutValidation("Editor-Plugin-Version", "copilot-chat/0.26.7");
        request.Headers.TryAddWithoutValidation("Copilot-Integration-Id", "vscode-chat");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    private static bool HasCopilotSnapshots(JsonElement root) =>
        TryProperty(root, out var snapshots, "quota_snapshots", "quotaSnapshots") && snapshots.ValueKind == JsonValueKind.Object;

    private static ProviderAutoResult ParseCopilot(JsonElement root)
    {
        var windows = new List<(string Label, decimal Percent, DateTimeOffset? Reset)>();
        var reset = DateProperty(root, "quota_reset_date_utc", "quota_reset_date", "quotaResetDateUtc");
        if (TryProperty(root, out var snapshots, "quota_snapshots", "quotaSnapshots") && snapshots.ValueKind == JsonValueKind.Object)
        {
            AddCopilotSnapshot(snapshots, windows, reset, "premium_interactions", "Premium");
            AddCopilotSnapshot(snapshots, windows, reset, "chat", "Chat");
            AddCopilotSnapshot(snapshots, windows, reset, "completions", "Completions");
        }
        if (windows.Count == 0)
            return Missing("copilot", "GitHub Copilotの利用枠が無効か、無制限プランです。") with { Status = "対象外" };

        var tightest = windows.MaxBy(item => item.Percent);
        var plan = StringProperty(root, "copilot_plan", "access_type_sku", "plan");
        var detail = string.Join(" / ", windows.Select(item => $"{item.Label} {item.Percent:0.#}%"));
        if (!string.IsNullOrWhiteSpace(plan)) detail += $" · {plan}";
        return new("copilot", true, tightest.Percent, 100, tightest.Reset, tightest.Label, detail);
    }

    private static void AddCopilotSnapshot(JsonElement snapshots, List<(string, decimal, DateTimeOffset?)> target, DateTimeOffset? reset, string name, string label)
    {
        if (!TryProperty(snapshots, out var snapshot, name) || snapshot.ValueKind != JsonValueKind.Object) return;
        if (TryProperty(snapshot, out var unlimited, "unlimited") && unlimited.ValueKind == JsonValueKind.True) return;

        var entitlement = DecimalProperty(snapshot, "entitlement");
        var remaining = DecimalProperty(snapshot, "remaining", "quota_remaining");
        var percentRemaining = DecimalProperty(snapshot, "percent_remaining", "percentRemaining");
        decimal? used = null;
        if (percentRemaining.HasValue)
            used = 100 - percentRemaining.Value;
        else if (entitlement is > 0 && remaining.HasValue)
            used = (entitlement.Value - remaining.Value) / entitlement.Value * 100;

        if (used is null) return;
        if (entitlement is 0m && percentRemaining is 100m) return;
        target.Add((label, Math.Clamp(used.Value, 0, 100), reset));
    }

    private async Task<IReadOnlyList<ProviderAutoResult>> FetchKimiAsync(CancellationToken cancellationToken)
    {
        try
        {
            var credential = LoadApiKey("kimi", "KIMI_CODE_API_KEY");
            string? plan = null;
            if (string.IsNullOrWhiteSpace(credential))
            {
                var local = await FindKimiCredentialAsync(cancellationToken);
                credential = local.Token;
                plan = local.Plan;
                if (local.Expired)
                    return [Missing("kimi", "Kimi Codeのログイン期限が切れています。Kimi CLIを起動して再ログインしてください。")];
            }
            if (string.IsNullOrWhiteSpace(credential))
                return [Missing("kimi", "Kimi Codeへログインするか、Kimi CodeのAPIキーを設定してください。")];

            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.kimi.com/coding/v1/usages");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await _http.SendAsync(request, cancellationToken);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return [Missing("kimi", "Kimi Codeの認証が切れています。Kimi CLIで再ログインするかAPIキーを更新してください。")];
            if (!response.IsSuccessStatusCode)
                return [Failed("kimi", $"Kimi Code usage API: HTTP {(int)response.StatusCode}")];

            using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
            return [ParseKimi(document.RootElement, plan)];
        }
        catch (Exception ex) { return [Failed("kimi", SafeMessage("Kimi Code", ex))]; }
    }

    private static ProviderAutoResult ParseKimi(JsonElement root, string? plan)
    {
        var windows = new List<(string Label, decimal Percent, DateTimeOffset? Reset)>();
        if (TryProperty(root, out var usage, "usage") && usage.ValueKind == JsonValueKind.Object)
            AddKimiDetail(usage, windows, "月次");
        if (TryProperty(root, out var limits, "limits") && limits.ValueKind == JsonValueKind.Array)
            foreach (var item in limits.EnumerateArray()) AddKimiLimit(item, windows, "利用枠");
        if (TryProperty(root, out var usages, "usages") && usages.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in usages.EnumerateArray())
            {
                var label = StringProperty(item, "scope") is { } scope && scope.Contains("CODING", StringComparison.OrdinalIgnoreCase)
                    ? "Kimi Code"
                    : "利用枠";
                if (TryProperty(item, out var detailSource, "detail")) AddKimiDetail(detailSource, windows, label);
                if (TryProperty(item, out var nestedLimits, "limits") && nestedLimits.ValueKind == JsonValueKind.Array)
                    foreach (var limit in nestedLimits.EnumerateArray()) AddKimiLimit(limit, windows, label);
                AddKimiRatio(item, windows);
            }
        }
        AddKimiRatio(root, windows);

        if (windows.Count == 0) return Failed("kimi", "Kimi Codeの利用枠レスポンスを判定できませんでした。");
        var tightest = windows.MaxBy(item => item.Percent);
        plan ??= StringProperty(root, "membership", "plan", "planName", "packageName", "level");
        var detail = string.Join(" / ", windows.Select(item => $"{item.Label} {item.Percent:0.#}%"));
        if (!string.IsNullOrWhiteSpace(plan)) detail += $" · {plan}";
        return new("kimi", true, tightest.Percent, 100, tightest.Reset, tightest.Label, detail);
    }

    private static void AddKimiDetail(JsonElement detail, List<(string, decimal, DateTimeOffset?)> target, string label)
    {
        var used = DecimalProperty(detail, "used", "usage");
        var limit = DecimalProperty(detail, "limit", "total");
        var remaining = DecimalProperty(detail, "remaining");
        if (used is null && limit is > 0 && remaining.HasValue) used = limit - remaining;
        if (used is null || limit is not > 0) return;
        var reset = DateProperty(detail, "resetTime", "reset_at", "resetAt");
        target.Add((label, used.Value / limit.Value * 100, reset));
    }

    private static void AddKimiLimit(JsonElement item, List<(string, decimal, DateTimeOffset?)> target, string fallbackLabel)
    {
        var label = fallbackLabel;
        if (TryProperty(item, out var window, "window") && window.ValueKind == JsonValueKind.Object)
        {
            var duration = DecimalProperty(window, "duration");
            var unit = StringProperty(window, "timeUnit", "unit") ?? "";
            if (duration.HasValue) label = KimiWindowLabel(duration.Value, unit) ?? label;
        }
        if (TryProperty(item, out var detail, "detail")) AddKimiDetail(detail, target, label);
        else AddKimiDetail(item, target, label);
    }

    private static string? KimiWindowLabel(decimal duration, string unit)
    {
        var minutes = unit.Contains("MINUTE", StringComparison.OrdinalIgnoreCase) ? duration
            : unit.Contains("HOUR", StringComparison.OrdinalIgnoreCase) ? duration * 60
            : unit.Contains("DAY", StringComparison.OrdinalIgnoreCase) ? duration * 1440
            : 0;
        if (minutes <= 0) return null;
        if (minutes is >= 290m and <= 310m) return "5時間";
        if (minutes is >= 10070m and <= 10090m) return "週次";
        if (minutes >= 28000m) return "月次";
        return minutes >= 1440m ? $"{minutes / 1440:0.#}日" : $"{minutes:0.#}分";
    }

    private static void AddKimiRatio(JsonElement element, List<(string, decimal, DateTimeOffset?)> target)
    {
        var ratio = DecimalProperty(element, "used_ratio", "usedRatio");
        if (ratio is null) return;
        var percent = ratio.Value <= 1 ? ratio.Value * 100 : ratio.Value;
        var reset = DateProperty(element, "resetTime", "reset_at", "resetAt");
        target.Add(("Kimi Code", percent, reset));
    }

    private async Task<IReadOnlyList<ProviderAutoResult>> FetchZaiAsync(CancellationToken cancellationToken)
    {
        try
        {
            var credentials = new List<(string Key, bool China)>();
            var stored = LoadApiKey("zai", "Z_AI_API_KEY");
            if (!string.IsNullOrWhiteSpace(stored)) credentials.Add((stored, false));
            var cnEnv = EnvironmentValue("BIGMODEL_API_KEY", "ZHIPU_API_KEY", "ZHIPUAI_API_KEY", "GLM_API_KEY");
            if (!string.IsNullOrWhiteSpace(cnEnv)) credentials.Add((cnEnv, true));
            foreach (var (key, china) in await FindZaiLocalCredentialsAsync(cancellationToken))
                credentials.Add((key, china));
            credentials = credentials
                .Where(item => !string.IsNullOrWhiteSpace(item.Key))
                .GroupBy(item => item.Key)
                .Select(group => group.First())
                .ToList();
            if (credentials.Count == 0)
                return [Missing("zai", "Z.ai / GLMのAPIキーを設定してください。")];

            foreach (var credential in credentials)
            {
                var result = await FetchZaiWithKeyAsync(credential.Key, credential.China, cancellationToken);
                if (result.Success || result.Status == "対象外") return [result];
            }
            return [Missing("zai", "Z.ai / GLMの利用枠を取得できません。APIキーを確認してください。")];
        }
        catch (Exception ex) { return [Failed("zai", SafeMessage("Z.ai", ex))]; }
    }

    private async Task<ProviderAutoResult> FetchZaiWithKeyAsync(string key, bool preferChina, CancellationToken cancellationToken)
    {
        var hosts = preferChina
            ? new[] { "https://open.bigmodel.cn", "https://api.z.ai" }
            : new[] { "https://api.z.ai", "https://open.bigmodel.cn" };
        ProviderAutoResult? last = null;
        foreach (var host in hosts)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{host}/api/monitor/usage/quota/limit");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await _http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                last = response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                    ? Missing("zai", "Z.ai / GLMのAPIキーを確認してください。")
                    : Failed("zai", $"Z.ai quota API: HTTP {(int)response.StatusCode}");
                continue;
            }

            using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
            var parsed = ParseZai(document.RootElement);
            if (parsed.Success || parsed.Status == "対象外") return parsed;
            last = parsed;
        }
        return last ?? Failed("zai", "Z.ai / GLMの利用枠を取得できませんでした。");
    }

    private static ProviderAutoResult ParseZai(JsonElement root)
    {
        if (TryProperty(root, out var success, "success") && success.ValueKind == JsonValueKind.False)
        {
            var message = StringProperty(root, "msg", "message") ?? "利用枠を取得できません";
            return Missing("zai", $"Z.ai / GLM: {message}");
        }
        if (!TryProperty(root, out var data, "data") || data.ValueKind != JsonValueKind.Object)
            return Failed("zai", "Z.ai / GLMのレスポンス形式を判定できませんでした。");
        if (!TryProperty(data, out var limits, "limits") || limits.ValueKind != JsonValueKind.Array)
            return Missing("zai", "GLM Coding Planが見つかりません。プランを確認してください。");

        var windows = new List<(string Label, decimal Percent, DateTimeOffset? Reset)>();
        foreach (var item in limits.EnumerateArray())
        {
            var type = StringProperty(item, "type") ?? "";
            if (type is not ("TOKENS_LIMIT" or "CREDIT_LIMIT" or "TIME_LIMIT")) continue;
            var percent = DecimalProperty(item, "percentage");
            var usage = DecimalProperty(item, "usage");
            var current = DecimalProperty(item, "currentValue", "current_value");
            var remaining = DecimalProperty(item, "remaining");
            if (usage is > 0)
            {
                var used = current ?? (remaining.HasValue ? usage.Value - remaining.Value : null);
                if (used.HasValue) percent = Math.Clamp(used.Value / usage.Value * 100, 0, 100);
            }
            if (percent is null) continue;

            var unit = DecimalProperty(item, "unit");
            var number = DecimalProperty(item, "number");
            var label = ZaiWindowLabel(type, unit, number);
            var reset = DateProperty(item, "nextResetTime", "next_reset_time", "resetTime");
            windows.Add((label, Math.Clamp(percent.Value, 0, 100), reset));
        }

        if (windows.Count == 0)
            return Missing("zai", "GLM Coding Planの利用枠が見つかりません。プランを確認してください。");

        var tightest = windows.MaxBy(item => item.Percent);
        var plan = StringProperty(data, "planName", "plan", "plan_type", "packageName", "level");
        var detail = string.Join(" / ", windows.Select(item => $"{item.Label} {item.Percent:0.#}%"));
        if (!string.IsNullOrWhiteSpace(plan)) detail += $" · {plan}";
        return new("zai", true, tightest.Percent, 100, tightest.Reset, tightest.Label, detail);
    }

    private static string ZaiWindowLabel(string type, decimal? unit, decimal? number)
    {
        if (type == "TIME_LIMIT") return unit is 5m && number is 1m ? "MCP月次" : "MCP";
        if (unit is null || number is null) return "利用枠";
        var minutes = unit switch
        {
            1m => number.Value * 1440,
            3m => number.Value * 60,
            5m => number.Value,
            6m => number.Value * 10080,
            _ => 0m
        };
        if (minutes is >= 290m and <= 310m) return "5時間";
        if (minutes is >= 10070m and <= 10090m) return "週次";
        if (minutes >= 28000m) return "月次";
        return minutes >= 1440m ? $"{minutes / 1440:0.#}日" : $"{minutes:0.#}分";
    }

    private async Task<IReadOnlyList<ProviderAutoResult>> FetchOpenRouterAsync(CancellationToken cancellationToken)
    {
        try
        {
            var key = LoadApiKey("openrouter", "OPENROUTER_API_KEY")
                ?? await FindOpenCodeAuthSecretAsync(["openrouter"], ["key", "apiKey", "access", "token"], cancellationToken);
            if (string.IsNullOrWhiteSpace(key))
                return [Missing("openrouter", "OpenRouterのAPIキーを設定してください。")];

            decimal? usage = null;
            decimal? limit = null;
            decimal? remaining = null;
            DateTimeOffset? reset = null;
            string? label = null;

            using (var request = OpenRouterRequest("https://openrouter.ai/api/v1/key", key))
            using (var response = await _http.SendAsync(request, cancellationToken))
            {
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    return [Missing("openrouter", "OpenRouterのAPIキーを確認してください。")];
                if (response.IsSuccessStatusCode)
                {
                    using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
                    var data = TryProperty(document.RootElement, out var nested, "data") ? nested : document.RootElement;
                    usage = DecimalProperty(data, "usage");
                    limit = DecimalProperty(data, "limit");
                    remaining = DecimalProperty(data, "limit_remaining", "limitRemaining");
                    reset = DateProperty(data, "limit_reset", "limitReset");
                    label = StringProperty(data, "label");
                }
            }

            if (limit is > 0)
            {
                var used = remaining.HasValue ? limit.Value - remaining.Value : usage ?? 0;
                var percent = Math.Clamp(used / limit.Value * 100, 0, 100);
                var detail = $"上限 ${usage ?? used:0.##}/${limit.Value:0.##}";
                if (remaining.HasValue) detail += $" · 残り ${remaining.Value:0.##}";
                if (!string.IsNullOrWhiteSpace(label)) detail += $" · {label}";
                return [new("openrouter", true, percent, 100, reset, "APIキー上限", detail)];
            }

            using (var request = OpenRouterRequest("https://openrouter.ai/api/v1/credits", key))
            using (var response = await _http.SendAsync(request, cancellationToken))
            {
                if (response.IsSuccessStatusCode)
                {
                    using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
                    var data = TryProperty(document.RootElement, out var nested, "data") ? nested : document.RootElement;
                    var total = DecimalProperty(data, "total_credits", "totalCredits");
                    var used = DecimalProperty(data, "total_usage", "totalUsage");
                    if (total.HasValue && used.HasValue)
                    {
                        var balance = total.Value - used.Value;
                        if (total.Value > 0)
                        {
                            var percent = Math.Clamp(used.Value / total.Value * 100, 0, 100);
                            return [new("openrouter", true, percent, 100, null, "クレジット",
                                $"使用 ${used.Value:0.##} / ${total.Value:0.##} · 残高 ${balance:0.##}")];
                        }
                        return [new("openrouter", true, balance, null, null, "クレジット残高",
                            $"残高 ${balance:0.##}", "自動", "USD", true)];
                    }
                }
            }

            return [Missing("openrouter", "OpenRouterのクレジット残高を取得できません。APIキーを確認してください。")];
        }
        catch (Exception ex) { return [Failed("openrouter", SafeMessage("OpenRouter", ex))]; }
    }

    private static HttpRequestMessage OpenRouterRequest(string url, string key)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("X-Title", "AI Usage Checker");
        return request;
    }

    private async Task<IReadOnlyList<ProviderAutoResult>> FetchDeepSeekAsync(CancellationToken cancellationToken)
    {
        try
        {
            var key = LoadApiKey("deepseek", "DEEPSEEK_API_KEY", "DEEPSEEK_KEY")
                ?? await FindOpenCodeAuthSecretAsync(["deepseek"], ["key", "apiKey", "access", "token"], cancellationToken);
            if (string.IsNullOrWhiteSpace(key))
                return [Missing("deepseek", "DeepSeekのAPIキーを設定してください。")];

            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.deepseek.com/user/balance");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await _http.SendAsync(request, cancellationToken);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return [Missing("deepseek", "DeepSeekのAPIキーを確認してください。")];
            if (!response.IsSuccessStatusCode)
                return [Failed("deepseek", $"DeepSeek balance API: HTTP {(int)response.StatusCode}")];

            using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
            return [ParseDeepSeek(document.RootElement)];
        }
        catch (Exception ex) { return [Failed("deepseek", SafeMessage("DeepSeek", ex))]; }
    }

    private static ProviderAutoResult ParseDeepSeek(JsonElement root)
    {
        if (!TryProperty(root, out var balances, "balance_infos", "balanceInfos") || balances.ValueKind != JsonValueKind.Array)
            return Failed("deepseek", "DeepSeekの残高レスポンスを判定できませんでした。");

        var entries = balances.EnumerateArray()
            .Select(item => new
            {
                Currency = StringProperty(item, "currency") ?? "USD",
                Total = DecimalProperty(item, "total_balance", "totalBalance"),
                Granted = DecimalProperty(item, "granted_balance", "grantedBalance"),
                ToppedUp = DecimalProperty(item, "topped_up_balance", "toppedUpBalance")
            })
            .Where(item => item.Total.HasValue)
            .ToList();
        if (entries.Count == 0) return Failed("deepseek", "DeepSeekの残高が見つかりませんでした。");

        var selected = entries.FirstOrDefault(item => item.Currency.Equals("USD", StringComparison.OrdinalIgnoreCase)) ?? entries[0];
        var detailParts = entries.Select(item => $"{item.Currency} {item.Total:0.##}").ToList();
        if (selected.ToppedUp.HasValue || selected.Granted.HasValue)
            detailParts.Add($"チャージ {selected.ToppedUp ?? 0:0.##} / 付与 {selected.Granted ?? 0:0.##}");
        if (TryProperty(root, out var available, "is_available", "isAvailable") && available.ValueKind == JsonValueKind.False)
            detailParts.Add("API利用不可");

        return new("deepseek", true, selected.Total, null, null, "残高", string.Join(" · ", detailParts),
            "自動", selected.Currency.ToUpperInvariant(), true);
    }

    private async Task<CopilotLocalAuth?> FindCopilotLocalAuthAsync(CancellationToken cancellationToken)
    {
        var paths = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "opencode", "auth.json"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "opencode", "auth.json"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "opencode", "auth.json"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "opencode", "auth.json")
        };
        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(path)) continue;
            try
            {
                using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path, cancellationToken));
                if (!TryProperty(document.RootElement, out var entry, "github-copilot") || entry.ValueKind != JsonValueKind.Object)
                    continue;
                var refresh = StringProperty(entry, "refresh", "refresh_token", "refreshToken");
                var access = StringProperty(entry, "access", "access_token", "accessToken", "token");
                var expires = DateProperty(entry, "expires", "expires_at", "expiresAt");
                if (expires is not null && expires <= DateTimeOffset.Now.AddMinutes(1)) access = null;
                if (!string.IsNullOrWhiteSpace(refresh) || !string.IsNullOrWhiteSpace(access))
                    return new(refresh, access);
            }
            catch { }
        }

        var ghHosts = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GitHub CLI", "hosts.yml"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "gh", "hosts.yml")
        };
        foreach (var path in ghHosts.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(path)) continue;
            try
            {
                var match = Regex.Match(await File.ReadAllTextAsync(path, cancellationToken), @"oauth_token:\s*([^\s#]+)");
                if (match.Success) return new(match.Groups[1].Value.Trim(), null);
            }
            catch { }
        }
        return null;
    }

    private async Task<(string? Token, string? Plan, bool Expired)> FindKimiCredentialAsync(CancellationToken cancellationToken)
    {
        foreach (var path in KimiAuthPaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(path)) continue;
            try
            {
                using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path, cancellationToken));
                var token = StringProperty(document.RootElement, "access_token", "accessToken", "access", "token");
                var plan = StringProperty(document.RootElement, "plan", "membership", "planName");
                var expires = DateProperty(document.RootElement, "expires_at", "expiresAt", "expiry");
                var expired = expires is not null && expires <= DateTimeOffset.Now.AddMinutes(1);
                if (!string.IsNullOrWhiteSpace(token)) return (token, plan, expired);
            }
            catch { }
        }
        return (null, null, false);
    }

    private static async Task<IReadOnlyList<(string Key, bool China)>> FindZaiLocalCredentialsAsync(CancellationToken cancellationToken)
    {
        var results = new List<(string, bool)>();
        var authPaths = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "opencode", "auth.json"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "opencode", "auth.json"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "opencode", "auth.json"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "opencode", "auth.json")
        };
        foreach (var path in authPaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(path)) continue;
            try
            {
                using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path, cancellationToken));
                foreach (var (name, china) in new[] { ("zai-coding-plan", false), ("zai", false), ("zhipu", true) })
                {
                    if (!TryProperty(document.RootElement, out var entry, name)) continue;
                    var key = entry.ValueKind == JsonValueKind.String
                        ? entry.GetString()
                        : StringProperty(entry, "key", "apiKey", "access", "token");
                    if (!string.IsNullOrWhiteSpace(key)) results.Add((key, china));
                }
            }
            catch { }
        }

        var files = new[]
        {
            (Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".coding-relay", "glm-api-key"), true),
            (Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "bigmodel", "api_key"), true),
            (Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "zhipu", "api_key"), true)
        };
        foreach (var (path, china) in files)
        {
            if (!File.Exists(path)) continue;
            try
            {
                var key = (await File.ReadAllTextAsync(path, cancellationToken)).Trim();
                if (!string.IsNullOrWhiteSpace(key)) results.Add((key, china));
            }
            catch { }
        }
        return results;
    }

    private static async Task<string?> FindOpenCodeAuthSecretAsync(string[] entryNames, string[] fieldNames, CancellationToken cancellationToken)
    {
        var paths = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "opencode", "auth.json"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "opencode", "auth.json"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "opencode", "auth.json"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "opencode", "auth.json"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".opencode", "auth.json")
        };
        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(path)) continue;
            try
            {
                using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path, cancellationToken));
                foreach (var name in entryNames)
                {
                    if (!TryProperty(document.RootElement, out var entry, name)) continue;
                    var value = entry.ValueKind == JsonValueKind.String
                        ? entry.GetString()
                        : StringProperty(entry, fieldNames);
                    if (!string.IsNullOrWhiteSpace(value)) return value;
                }
            }
            catch { }
        }
        return null;
    }

    private static string? LoadApiKey(string providerId, params string[] envNames)
    {
        var stored = new ConnectionSettingsStore().LoadApiKey(providerId);
        if (!string.IsNullOrWhiteSpace(stored)) return NormalizeApiKey(stored);
        return NormalizeApiKey(EnvironmentValue(envNames));
    }

    private static string? NormalizeApiKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var key = value.Trim().Trim('"', '\'');
        if (key.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) key = key[7..].Trim();
        return string.IsNullOrWhiteSpace(key) ? null : key;
    }

    private static string? EnvironmentValue(params string[] names)
    {
        foreach (var name in names)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
        }
        return null;
    }

    private static DateTimeOffset? NowPlusSeconds(JsonElement element, params string[] names)
    {
        var seconds = DecimalProperty(element, names);
        return seconds.HasValue ? DateTimeOffset.Now.AddSeconds((double)seconds.Value) : null;
    }

    private sealed record CopilotLocalAuth(string? Refresh, string? Access);
}
