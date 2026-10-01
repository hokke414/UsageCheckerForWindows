using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AIUsageChecker;

public sealed class ConnectionSettingsStore
{
    private const string CredentialTarget = "AIUsageChecker/Notion";
    private readonly string _settingsPath;

    public ConnectionSettingsStore()
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AIUsageChecker");
        Directory.CreateDirectory(directory);
        _settingsPath = Path.Combine(directory, "connections.json");
    }

    public string? LoadNotionToken() => WindowsCredential.Read(CredentialTarget);

    public void SaveNotionToken(string input)
    {
        var token = ExtractNotionToken(input);
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("token_v2を確認できません。NotionのCopy as cURL、Cookieヘッダー、またはtoken_v2値を貼り付けてください。");
        WindowsCredential.Write(CredentialTarget, token);
    }

    public void RemoveNotionToken() => WindowsCredential.Delete(CredentialTarget);

    public async Task<ConnectionSettings> LoadAsync()
    {
        try
        {
            if (!File.Exists(_settingsPath)) return new();
            await using var stream = File.OpenRead(_settingsPath);
            return await JsonSerializer.DeserializeAsync<ConnectionSettings>(stream) ?? new();
        }
        catch { return new(); }
    }

    public async Task SaveAsync(ConnectionSettings settings)
    {
        var temporary = _settingsPath + ".tmp";
        await using (var stream = File.Create(temporary))
            await JsonSerializer.SerializeAsync(stream, settings, new JsonSerializerOptions { WriteIndented = true });
        File.Move(temporary, _settingsPath, true);
    }

    internal static string? ExtractNotionToken(string input)
    {
        var value = input.Trim();
        if (string.IsNullOrWhiteSpace(value)) return null;
        var match = Regex.Match(value, @"token_v2\s*=\s*([^;\s'\""\\]+)", RegexOptions.IgnoreCase);
        if (match.Success) return Uri.UnescapeDataString(match.Groups[1].Value.Trim());
        if (!value.Contains(' ') && !value.Contains(':') && value.Length > 20) return value.Trim('"', '\'');
        return null;
    }
}

public sealed class ConnectionSettings
{
    public string NotionWorkspaceId { get; set; } = "";
}

internal static class WindowsCredential
{
    private const int Generic = 1;
    private const int LocalMachine = 2;

    public static void Write(string target, string secret)
    {
        var bytes = Encoding.Unicode.GetBytes(secret);
        var blob = Marshal.AllocCoTaskMem(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new NativeCredential
            {
                Type = Generic,
                TargetName = target,
                CredentialBlobSize = bytes.Length,
                CredentialBlob = blob,
                Persist = LocalMachine,
                UserName = Environment.UserName
            };
            if (!CredWrite(ref credential, 0)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
        finally { Marshal.FreeCoTaskMem(blob); }
    }

    public static string? Read(string target)
    {
        if (!CredRead(target, Generic, 0, out var pointer)) return null;
        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(pointer);
            return credential.CredentialBlob == IntPtr.Zero
                ? null
                : Marshal.PtrToStringUni(credential.CredentialBlob, credential.CredentialBlobSize / 2);
        }
        finally { CredFree(pointer); }
    }

    public static void Delete(string target) => CredDelete(target, Generic, 0);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public int Flags;
        public int Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite([In] ref NativeCredential userCredential, [In] uint flags);
    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, int type, int flags, out IntPtr credentialPtr);
    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, int type, int flags);
    [DllImport("advapi32.dll", EntryPoint = "CredFree", SetLastError = true)]
    private static extern void CredFree([In] IntPtr cred);
}
