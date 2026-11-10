# Changelog

## 1.5.0 - 2026-11-10

- Added a manual update checker in Settings that downloads the latest Portable build, verifies its SHA-256, and swaps the running exe before relaunching.

## 1.4.0 - 2026-11-10

- Updated Notion AI for the November 2026 Standard, Select, and Notion credit system: reads resolved `auto` / `flex` allowances for Standard and Select, falls back to the legacy Rolling / Monthly windows, and shows the Notion credit balance from the optional eligibility endpoint.
- Added OpenCode Go subscription usage for rolling, weekly, and monthly windows.
- Added GitHub Copilot, Kimi Code, and Z.ai GLM Coding Plan usage tracking with local credential discovery.
- Added OpenRouter credit usage and DeepSeek balance cards.
- Added optional API-key fields stored in Windows Credential Manager for OpenCode Go, Kimi Code, Z.ai, OpenRouter, and DeepSeek.
- Added balance-only card rendering so credit balances hide the percentage progress bar.

## 1.3.0 - 2026-10-02

- Changed the primary action to a pale blue surface with dark text for improved readability.
- Removed the manual usage editor, its sidebar, and all manual record actions.
- Legacy manual values are no longer restored; only automatically checked values remain cached.
- Reduced the dashboard to a compact three-column grid with provider logos and essential usage data only.
- Removed global connection text, provider subtitles, status tags, footer copy, and official-page buttons.
- Reduced the Settings and Refresh button sizes for the desktop layout.

## 1.2.0 - 2026-10-01

- Added provider visibility settings and skipped background checks for hidden providers.
- Added app-login flows for Notion AI and Gemini Apps.
- Added automatic Gemini Apps usage parsing with Gemini CLI fallback.
- Added a Windows app icon and two single-file Release variants.
- Improved startup stability and crash logging.
- Refreshed the interface using Digital Agency Design System accessibility and layout guidance.
- Added public-release privacy documentation and automated GitHub Release packaging.
