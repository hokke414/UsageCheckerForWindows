# AI Usage Checker for Windows

Claude、ChatGPT / Codex、Notion AI、Cursor（Grok Botを含む）、Geminiの利用枠を、小さなWindowsウィンドウでまとめて確認する非公式デスクトップアプリです。

## ダウンロード

[Releases](../../releases) から用途に合うexeをダウンロードしてください。

- `AIUsageChecker-Windows-x64-Portable.exe`: .NETランタイム同梱。通常はこちらを推奨します。
- `AIUsageChecker-Windows-x64-Lite.exe`: 小容量版。Microsoft .NET 10 Desktop Runtimeが必要です。

Windows SmartScreenが表示される場合があります。本プロジェクトのReleaseファイルとSHA-256値を確認してから実行してください。現在の配布物はコード署名されていません。

## 主な機能

- 起動時と5分ごとの自動チェック
- ［今すぐ確認］による手動更新
- 設定画面でサービスごとに表示・非表示を選択
- 非表示サービスのバックグラウンド取得を停止
- Notion AIとGemini Appsのアプリ内ログイン
- ブランドロゴ、使用率、残量、リセット時刻だけをコンパクトなカードで表示
- オフライン時も前回の取得結果を表示
- 予期しない終了の診断ログ

UIは[デジタル庁デザインシステム](https://design.digital.go.jp/dads/)の色、タイポグラフィ、余白、フォーカス表現を参考にしています。本アプリはデジタル庁の公式製品ではありません。

## 自動取得の対応状況

| サービス | 自動取得 | 取得元 |
|---|---:|---|
| ChatGPT / Codex | 対応 | ログイン済みCodexのローカル認証 |
| Claude | 対応 | ログイン済みClaude Codeのローカル認証 |
| Cursor | 対応 | CursorのローカルセッションとUsage API |
| Grok Bot (Cursor) | 対応 | Cursor DashboardのGrok Bot枠 |
| Notion AI | 対応 | アプリ内ログインまたは明示的に登録したNotionセッション |
| Gemini | 対応 | Gemini AppsのUsage画面。Gemini CLI OAuthクォータをフォールバック利用 |

自動取得できなかった場合も前回値を維持し、カードへ理由を表示します。各社の非公開APIや画面構造に依存する連携は、サービス側の変更で一時的に利用できなくなることがあります。

## 初回設定

### Notion AI

1. ［設定］を開きます。
2. ［Notionにログイン］を押し、表示されたNotion画面でログインします。
3. ログイン完了後、セッションはWindows資格情報マネージャーへ保存されます。

自動ログインを利用できない場合は、NotionのUsageリクエストを開発者ツールから「Copy as cURL」でコピーし、接続欄へ貼り付けることもできます。取得対象は対応プランのNotion AI利用枠です。

### Gemini

1. ［設定］を開きます。
2. ［Geminiにログイン］を押し、Googleアカウントでログインします。
3. GeminiのUsage画面が開いたら［完了］を押します。

以後、起動時と5分ごとに `gemini.google.com/usage` の表示から5時間・週次枠を読み取ります。Geminiを非表示にするとWebViewも起動しません。Gemini CLIへログイン済みの場合は、Apps側を取得できなかったときにCode Assistクォータを利用します。

## プライバシー

このリポジトリとRelease exeには、開発者のログイン情報、利用履歴、ブラウザプロファイル、ローカルパスを含めません。アプリには分析・広告・開発者運営サーバーへの送信機能もありません。

既存のCodex、Claude Code、Cursor、Gemini CLI認証はローカルで読み取り、各サービス自身の使用量取得にだけ使います。Notionセッションは明示的な接続時だけWindows資格情報マネージャーへ保存します。詳細と削除方法は [PRIVACY.md](PRIVACY.md) を参照してください。

## ソースから実行

必要環境はWindows x64、.NET 10 SDK、WebView2 Runtimeです。

```powershell
dotnet restore UsageChecker.csproj
dotnet run --project UsageChecker.csproj
```

## Releaseビルド

ポータブル版:

```powershell
dotnet publish UsageChecker.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:PublishDir=bin\portable\
```

軽量版:

```powershell
dotnet publish UsageChecker.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=false -p:PublishDir=bin\lite\
```

`v*`タグをpushするとGitHub Actionsが両方のexeとSHA-256一覧をReleaseへ公開します。

## ローカルデータ

- 設定と利用量キャッシュ: `%LOCALAPPDATA%\AIUsageChecker`
- Notion認証: Windows資格情報マネージャーの `AIUsageChecker/Notion`
- クラッシュログ: `%LOCALAPPDATA%\AIUsageChecker\crash.log`

## ライセンス

[MIT License](LICENSE)。外部実装の参照元は [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) に記載しています。
