# Security policy

## Reporting a vulnerability

Please open a GitHub security advisory for vulnerabilities. Do not include access tokens, session cookies, account IDs, email addresses, or full crash logs in a public issue.

## Credential handling

The app never bundles credentials into its executable. Existing Codex, Claude Code, Cursor, and Gemini CLI sessions are read locally and used only against their respective provider. A Notion session is stored with Windows Credential Manager. Browser sessions are isolated under the app's local WebView2 directory.

Release builds disable debug symbols. Repository rules exclude build output, local settings, logs, browser profiles, and key files.
