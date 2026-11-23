# Timeshift Viewer

キャプチャボードの映像と音声を表示しながら、直近の映像（既定10分）をメモリに保持して巻き戻し再生できる Windows アプリ。古い分は自動で破棄する。

## ビルド

必要なもの: [.NET SDK](https://dotnet.microsoft.com/download)（6 以降。ターゲットは .NET Framework 4.8）

```
dotnet build -c Release
```

出力: `bin\Release\net48\TimeshiftViewer.exe`（単体で動作。Windows 10/11 標準の .NET Framework 4.8 で動く）

Windows では参照アセンブリ（Microsoft.NETFramework.ReferenceAssemblies）が NuGet から自動取得される。Linux では mono-devel を入れれば `/usr/lib/mono/4.8-api` を参照してビルドできる。

## 実行時の動作

初回起動時に ffmpeg と mpv を exe と同じフォルダの `tools\` にダウンロードする。

## 操作（プレーヤー画面）

| 操作 | 内容 |
|---|---|
| 画面下のボタン | ✎ / « 10s / « 5s / 一時停止 / 5s » / 10s » / LIVE / シミュ（連打すると積み上げ） |
| Space / 右クリック | 一時停止・再開 |
| ← → / Shift+← → | 5秒 / 10秒 移動 |
| J | 60秒戻る |
| Home / End | 最古へ / ライブへ |
| , . | コマ送り |
| ↑↓ / ホイール | 音量 |
| M / F | ミュート / 全画面 |
| P / 1〜5 / Ctrl+Z / C | ペン / 色 / 1つ戻す / 全消し（ペン中は画面右端のパレットでも操作できる） |
| T | シミュレーターを映像の上に重ねて開く（今の画面の盤面とツモを読み込む） |
| Tab / S | 操作パネルを隠す / スクリーンショット |

## ぷよぷよeスポーツ向けの機能

- 対戦の開始・終了を映像から検知し、シークバーに試合の区間と「名前・勝利数・セット数・結果」を表示
- 盤面・NEXT の画像認識、ツモのパターン（シード値）の識別（絞り込めなければ候補から無作為に選ぶ）
- シミュレーター（ツモ順の操作、連鎖・得点、Undo/分岐、比較）

## テスト

```
dotnet run --project Tests -c Release
```
