# AI Multi Window

Windows上で、1つの大きな親ウィンドウの中に複数のWeb画面を並べて使うためのシンプルなデスクトップアプリです。

## 主な機能

- 1 / 2 / 3 / 4 分割レイアウト
- 2分割は左右、3分割は左大+右上下、4分割は2x2
- 各ペインごとにURLを指定
- ChatGPT / Gemini / Claude などのWebサービスを表示可能
- 各ペインに 戻る / 進む / 再読込 / ホーム ボタン
- ペイン境界をドラッグしてサイズ調整
- 前回のレイアウトとURLを自動保存・復元
- Ctrl+1〜4 でレイアウト切替
- F11 で全画面表示
- Windows 10 / 11 対応

## AIチャット操作 V1

各Webペインの上部にAI操作バーを追加しています。

- メッセージを入力して「送信」で、表示中のAIチャットへ直接入力・送信
- `Ctrl+Enter` でも送信可能
- 「最新回答コピー」で、現在表示中のAIの最新回答をクリップボードへコピー
- ChatGPTを優先したDOM検出に加え、一般的なtextarea/contenteditableにもフォールバック
- APIは使わず、WebView2上の表示ページを操作するため追加API料金は不要

### 使い方

1. ペインで `https://chatgpt.com/` を開いてログイン
2. AI操作バーに送りたい文章を入力
3. 「送信」または `Ctrl+Enter`
4. ChatGPTの回答後に「最新回答コピー」
5. コピーした回答を司令塔ペインへ貼り付ける

> Webサービス側の画面構造が変更されると、送信・回答取得のセレクタ調整が必要になる場合があります。また、各サービスの利用規約や自動操作に関するルールに従って利用してください。

## 必要環境

- Windows 10/11
- .NET 8 SDK
- Microsoft Edge WebView2 Runtime
  - Windows 11には通常プリインストールされています

## 実行

```powershell
dotnet restore
dotnet run
```

または `run.bat` をダブルクリックしてください。

## EXEを作る

```powershell
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true
```

または `build.bat` をダブルクリックしてください。

生成物:

`bin\Release\net8.0-windows\win-x64\publish\AiMultiWindow.exe`

## V1検証

Release build、ワークフロー状態遷移、PATCH/CREATE/MODIFY、Step単位rollback、Coder repairの回帰テストをまとめて実行できます。

```powershell
powershell -ExecutionPolicy Bypass -File scripts/verify-v1.ps1
```

## 初期URL

1. ChatGPT: https://chatgpt.com/
2. Gemini: https://gemini.google.com/
3. Claude: https://claude.ai/
4. Google: https://www.google.com/

URL欄は自由に変更できます。変更内容は自動保存されます。

## 次に追加する予定の機能

- 複数ペインへの一括・並列送信
- TASK_ID / WORKER_ID付きタスク管理
- 回答完了の自動検知
- Worker回答の自動回収
- 司令塔ペインへの自動返却
- 無料枠を意識した実行キュー

## 注意

Webサービス側の仕様、認証、セキュリティポリシー変更によって、特定サイトがWebView2内でログインや表示を制限する場合があります。その場合はアプリ側の不具合ではなく、サービス側の制約である可能性があります。

## License

MIT
