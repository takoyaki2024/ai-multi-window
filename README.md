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

## 初期URL

1. ChatGPT: https://chatgpt.com/
2. Gemini: https://gemini.google.com/
3. Claude: https://claude.ai/
4. Google: https://www.google.com/

URL欄は自由に変更できます。変更内容は自動保存されます。

## 注意

Webサービス側の仕様、認証、セキュリティポリシー変更によって、特定サイトがWebView2内でログインや表示を制限する場合があります。その場合はアプリ側の不具合ではなく、サービス側の制約である可能性があります。

## License

MIT
