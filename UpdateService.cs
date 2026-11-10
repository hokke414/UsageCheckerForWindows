using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AIUsageChecker;

public sealed record UpdateInfo(
    bool IsAvailable,
    string? Tag,
    string? Body,
    string? PortableUrl,
    string? ChecksumsUrl);

public sealed class UpdateService
{
    private const string ReleaseApi = "https://api.github.com/repos/hokke414/UsageCheckerForWindows/releases/latest";
    private const string PortableAsset = "AIUsageChecker-Windows-x64-Portable.exe";
    private const string ChecksumsAsset = "SHA256SUMS.txt";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(15) };

    public static string CurrentVersion
    {
        get
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
            return $"{version.Major}.{version.Minor}.{version.Build}";
        }
    }

    public async Task<UpdateInfo> CheckAsync(CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ReleaseApi);
        request.Headers.UserAgent.ParseAdd("AIUsageChecker-Updater");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var response = await Http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"GitHub API: HTTP {(int)response.StatusCode}");

        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
        var root = document.RootElement;
        var tag = StringProperty(root, "tag_name");
        var body = StringProperty(root, "body");

        string? portableUrl = null;
        string? checksumsUrl = null;
        if (TryProperty(root, out var assets, "assets") && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var name = StringProperty(asset, "name");
                var url = StringProperty(asset, "browser_download_url");
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(url)) continue;
                if (name.Equals(PortableAsset, StringComparison.OrdinalIgnoreCase)) portableUrl = url;
                else if (name.Equals(ChecksumsAsset, StringComparison.OrdinalIgnoreCase)) checksumsUrl = url;
            }
        }

        var current = ComparableVersion(Assembly.GetExecutingAssembly().GetName().Version);
        var latest = ParseTag(tag);
        var available = latest is not null && current is not null && latest > current;
        return new(available, tag, body, portableUrl, checksumsUrl);
    }

    public async Task InstallAsync(UpdateInfo info, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(info.PortableUrl))
            throw new InvalidOperationException("更新用のexeが見つかりませんでした。");

        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AIUsageChecker", "update");
        Directory.CreateDirectory(directory);
        var newExe = Path.Combine(directory, "AIUsageChecker.exe");

        using (var download = await Http.GetStreamAsync(info.PortableUrl, cancellationToken))
        using (var file = File.Create(newExe))
            await download.CopyToAsync(file, cancellationToken);

        if (!string.IsNullOrWhiteSpace(info.ChecksumsUrl))
        {
            var checksumFile = await Http.GetStringAsync(info.ChecksumsUrl, cancellationToken);
            var expected = ParseChecksum(checksumFile, PortableAsset);
            if (expected is not null)
            {
                var actual = ComputeSha256(newExe);
                if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                {
                    try { File.Delete(newExe); } catch { }
                    throw new InvalidOperationException("ダウンロードしたファイルの検証に失敗しました。");
                }
            }
        }

        var target = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(target))
            throw new InvalidOperationException("実行中ファイルのパスを特定できませんでした。");

        LaunchSwap(target, newExe);
    }

    private static void LaunchSwap(string targetExe, string newExe)
    {
        var script = Path.Combine(Path.GetDirectoryName(newExe) ?? "", "update.cmd");
        var builder = new StringBuilder();
        builder.AppendLine("@echo off");
        builder.AppendLine("setlocal enabledelayedexpansion");
        builder.AppendLine($"set \"NEW={newExe}\"");
        builder.AppendLine($"set \"TARGET={targetExe}\"");
        builder.AppendLine("set /a N=0");
        builder.AppendLine(":retry");
        builder.AppendLine("ping -n 2 127.0.0.1 >nul");
        builder.AppendLine("move /y \"%NEW%\" \"%TARGET%\" >nul 2>&1");
        builder.AppendLine("if not errorlevel 1 goto done");
        builder.AppendLine("set /a N+=1");
        builder.AppendLine("if !N! LSS 40 goto retry");
        builder.AppendLine("start \"\" explorer /select,\"%NEW%\"");
        builder.AppendLine("exit /b 1");
        builder.AppendLine(":done");
        builder.AppendLine("start \"\" \"%TARGET%\"");
        builder.AppendLine("del \"%~f0\"");
        File.WriteAllText(script, builder.ToString());

        Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{script}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        });
    }

    private static Version? ParseTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var trimmed = tag.Trim().TrimStart('v', 'V');
        return Version.TryParse(trimmed, out var parsed) ? ComparableVersion(parsed) : null;
    }

    private static Version? ComparableVersion(Version? version) =>
        version is null ? null : new Version(version.Major, version.Minor, version.Build);

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        var hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string? ParseChecksum(string content, string fileName)
    {
        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && parts[^1].Equals(fileName, StringComparison.OrdinalIgnoreCase))
                return parts[0];
        }
        return null;
    }

    private static bool TryProperty(JsonElement element, out JsonElement value, params string[] names)
    {
        if (element.ValueKind == JsonValueKind.Object)
            foreach (var name in names)
                if (element.TryGetProperty(name, out value)) return true;
        value = default;
        return false;
    }

    private static string? StringProperty(JsonElement element, params string[] names) =>
        TryProperty(element, out var value, names) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
