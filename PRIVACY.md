# Privacy

AI Usage Checker runs locally on Windows. It has no developer-operated server, analytics, advertising, telemetry, or account database.

## Data the app reads

When a provider is visible and automatic checking is enabled, the app may read an existing local login session for that provider:

- ChatGPT / Codex: `%USERPROFILE%\.codex\auth.json`
- Claude Code: `%USERPROFILE%\.claude\.credentials.json`
- Cursor and Grok Bot: `%APPDATA%\Cursor\User\globalStorage\state.vscdb`
- Gemini CLI: `%USERPROFILE%\.gemini\oauth_creds.json`
- OpenCode: `%LOCALAPPDATA%\opencode\auth.json`, `%APPDATA%\opencode\auth.json`, or `%USERPROFILE%\.local\share\opencode\auth.json`
- GitHub Copilot / GitHub CLI: OpenCode's `github-copilot` auth entry or `%APPDATA%\GitHub CLI\hosts.yml`
- Kimi Code CLI: `%USERPROFILE%\.kimi-code\credentials\kimi-code.json` or `%USERPROFILE%\.kimi\credentials\kimi-code.json`
- Z.ai / GLM: OpenCode auth entries such as `zai-coding-plan`, or local key files under `%USERPROFILE%\.config`
- Notion and Gemini Apps: a separate WebView2 profile created by this app

These credentials are used only to request usage information from the corresponding provider. The app does not send them to the project author or to any unrelated service.

API keys can also be supplied through the Settings window. They are stored only in Windows Credential Manager and are never written to the usage cache or repository.

When you press 更新を確認 in Settings, the app calls the GitHub Releases API for this repository to compare versions. It does not send any local data or credentials; only a plain HTTPS request to `api.github.com` is made, and the selected Portable build is downloaded from the release assets.

## Data the app stores

- Usage cache, display choices, and non-secret connection settings: `%LOCALAPPDATA%\AIUsageChecker`
- Notion `token_v2`: Windows Credential Manager under `AIUsageChecker/Notion`
- Optional API keys: Windows Credential Manager under `AIUsageChecker/ProviderApiKey/<provider>`
- Notion and Gemini browser sessions: `%LOCALAPPDATA%\AIUsageChecker\WebView2`
- Update downloads: `%LOCALAPPDATA%\AIUsageChecker\update` (new exe and its helper script, removed after a successful swap)
- Crash details, if an unexpected error occurs: `%LOCALAPPDATA%\AIUsageChecker\crash.log`

No credential, browser profile, cached usage value, or crash log is included in the source repository or Release executables.

## Remove local data

Use the app's Notion disconnect button and each API-key 解除 button first. Then close the app and delete `%LOCALAPPDATA%\AIUsageChecker`. If needed, remove the `AIUsageChecker/Notion` and `AIUsageChecker/ProviderApiKey/*` generic credentials in Windows Credential Manager.

## Provider APIs

Some integrations depend on undocumented endpoints or rendered web pages and may stop working when a provider changes its service. Review each provider's own privacy policy before connecting it.
