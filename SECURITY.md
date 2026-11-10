# Security policy

## Reporting a vulnerability

Please open a GitHub security advisory for vulnerabilities. Do not include access tokens, session cookies, account IDs, email addresses, or full crash logs in a public issue.

## Credential handling

The app never bundles credentials into its executable. Existing Codex, Claude Code, Cursor, Gemini CLI, OpenCode, GitHub CLI, and Kimi Code CLI sessions are read locally and used only against their respective provider. Notion sessions and optional API keys entered in Settings are stored with Windows Credential Manager. Browser sessions are isolated under the app's local WebView2 directory.

The updater downloads releases only from this repository's GitHub Releases assets and verifies each downloaded executable against the published `SHA256SUMS.txt` before replacing the running file.

Release builds disable debug symbols. Repository rules exclude build output, local settings, logs, browser profiles, and key files.
