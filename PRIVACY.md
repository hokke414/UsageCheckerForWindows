# Privacy

AI Usage Checker runs locally on Windows. It has no developer-operated server, analytics, advertising, telemetry, or account database.

## Data the app reads

When a provider is visible and automatic checking is enabled, the app may read an existing local login session for that provider:

- ChatGPT / Codex: `%USERPROFILE%\.codex\auth.json`
- Claude Code: `%USERPROFILE%\.claude\.credentials.json`
- Cursor and Grok Bot: `%APPDATA%\Cursor\User\globalStorage\state.vscdb`
- Gemini CLI: `%USERPROFILE%\.gemini\oauth_creds.json`
- Notion and Gemini Apps: a separate WebView2 profile created by this app

These credentials are used only to request usage information from the corresponding provider. The app does not send them to the project author or to any unrelated service.

## Data the app stores

- Usage cache, display choices, and non-secret connection settings: `%LOCALAPPDATA%\AIUsageChecker`
- Notion `token_v2`: Windows Credential Manager under `AIUsageChecker/Notion`
- Notion and Gemini browser sessions: `%LOCALAPPDATA%\AIUsageChecker\WebView2`
- Crash details, if an unexpected error occurs: `%LOCALAPPDATA%\AIUsageChecker\crash.log`

No credential, browser profile, cached usage value, or crash log is included in the source repository or Release executables.

## Remove local data

Use the app's Notion disconnect button first. Then close the app and delete `%LOCALAPPDATA%\AIUsageChecker`. If needed, remove the `AIUsageChecker/Notion` generic credential in Windows Credential Manager.

## Provider APIs

Some integrations depend on undocumented endpoints or rendered web pages and may stop working when a provider changes its service. Review each provider's own privacy policy before connecting it.
