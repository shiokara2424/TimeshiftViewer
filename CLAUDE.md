# CLAUDE.md — Timeshift Viewer 開発メモ

## 目的
キャプチャボード（ユーザー環境: I-O DATA GV-4K60/PCIE、DirectShow 名 `GV-4K60_PCIE, Video 01 Capture`）の映像を
ライブ表示しつつ、直近 N 分（既定10分）を巻き戻し・一時停止・コマ送りできるようにする。古いデータは捨てる。
ユーザーは **ぷよぷよeスポーツの配信中の見直し** に使う。配信は OBS で「Timeshift Viewer」ウィンドウをキャプチャしているので、
**見せたいもの（ペン・シミュレーター・試合情報）は全部このウィンドウの中に出す**こと。

## 構成
```
TimeshiftViewer.exe (C#, .NET Framework 4.8, WinForms)
  ├ MainForm   : 設定画面・初回ダウンロード
  ├ PlayerForm : プレーヤーのウィンドウ。mpv を --wid で埋め込み、その上に SimPanel を重ねる
  ├ PlayerSession : 再生中の処理
  │   ├ ffmpeg.exe : DirectShow 取り込み → 出力1: H.264+AAC の MPEG-TS（stdout → mpv stdin に中継）
  │   │                               → 出力2: 解析用の切り出し BGR 画像 10fps（名前付きパイプ → AnalysisPipe）
  │   ├ mpv.exe    : stdin を再生。demuxer キャッシュが巻き戻しバッファ。JSON IPC で双方向にやり取り（MpvIpc）
  │   │     └ scripts/timeshift.lua : OSD（バッジ/シークバー/ボタン/試合表示）、ペン、ライブ追従
  │   └ MatchAnalyzer : 対戦の開始・終了・ツモの検知 → 試合一覧を lua に送ってシークバーに表示
  └ Puyo/ : シミュレーター、ツモ生成と識別、画像認識
```

| ファイル | 内容 |
|---|---|
| `Core.cs` | `Paths`、`AppSettings`（settings.ini）、`ToolInstaller`、`FF`（ffmpeg 実行・デバイス列挙・エンコーダ検出・引数生成） |
| `MainForm.cs` | 設定画面、セッション開始（エンコーダ決定→デバイス確認→PlayerSession）、`Program.Main`（起動オプション） |
| `PlayerForm.cs` | 埋め込みプレーヤー。全画面は mpv の `fullscreen` プロパティを observe して自分で切り替える。シミュレーターの重ね表示・左右切替・盤面読み込みボタン。フォーカスが自分にあるときのキーは IPC の keypress で mpv に送る |
| `PlayerSession.cs` | ffmpeg/mpv の起動と終了監視、解析用出力の追加、mpv との時刻合わせ（demuxer-cache-time を 0.5 秒ごとに取得）、試合一覧の送信、シミュレーターへの盤面読み込み（screenshot-to-file → 認識 → 試合のツモと手数） |
| `MpvIpc.cs` | mpv の JSON IPC（命令と応答、client-message、property-change） |
| `Analyzer.cs` | `MatchAnalyzer`（開始: ゴーのオレンジ＋両盤面がほぼ空 / 終了: 勝利数が増えた / ツモ: NEXT 欄が動いて落ち着いたら1手）、`AnalysisPipe`（受信と解析を分け、解析は最新の1枚だけ。遅れると ffmpeg ごと止まるため） |
| `mpvconf/*` | exe に埋め込み、起動時に `mpv-config/` へ書き出す |
| `Puyo/Tsumo.cs` | ぷよぷよeスポーツ（対戦）のツモ生成とシード値の識別（仕様: ユーザー提供の puyo_tsumo_spec.md）。絞り込めなければ候補から一様に無作為に選ぶ（リザーバサンプリング） |
| `Puyo/Field.cs` `SimState.cs` | 盤面・連鎖・通ルールの得点、局面ツリー（Undo/Redo/分岐） |
| `Puyo/SimPanel.cs` `SimForm.cs` `PuyoViews.cs` `TsumoDialog.cs` | シミュレーター画面（SimPanel は埋め込み用の部品、SimForm は単独ウィンドウ） |
| `Puyo/Recognizer.cs` | 画像認識。座標は 1920×1080 基準で固定（`PuyoLayout`）。ぷよの色は画素ごとの HSV 多数決、数字は TW×TH の形の見本と比較 |
| `Puyo/DigitTemplates*.cs` | 数字の見本（`Tests --make-templates samples Puyo/DigitTemplates.Data.cs` で samples/ のスコア欄から生成） |
| `Puyo/NameOcr.cs` | プレイヤー名（Windows.Media.Ocr。Microsoft.Windows.SDK.Contracts を参照。en-US の認識エンジンがないと「1」と「I」を取り違える） |
| `Tests/` | ロジックと認識のテスト（下記） |
| `samples/` | 認識の調整・テスト用の画像（1080p。`analysis_frame_sets.png` は実際の解析用出力 1520×880、`synthetic_go_numbers.jpg` は合成） |

## ビルドと確認
- `dotnet build -c Release`（Release ビルド後に exe をこのフォルダ直下＝実行用フォルダへコピーする。起動中だとコピーは警告になり失敗する）
- テスト: `dotnet run --project Tests -c Release`（ツモ生成の正解データ・識別・連鎖・履歴・画像認識・対戦の検知）
  - 開発用: `PuyoTests.exe --recog <画像...>`（認識結果の表示）、`--px <画像> x1 y1 x2 y2`（色相の分布）、`--make-templates <samples> <出力.cs>`
- 起動オプション（開発用）: `--test-input=<動画>`（キャプチャの代わりに動画をループ再生）、`--autostart`、`--sim [--tsumo=4:21926]`（シミュレーターだけ）
- 環境変数（開発用）: `TSV_DEBUG=1`（debug-matches.txt に解析状況と試合一覧、debug-layout.txt に配置）、`TSV_DUMP=1`（解析用画像を debug-frame.png に1枚保存）、`TSV_MPV_EXTRA`（mpv の追加引数。例 `--log-file=...`）
- 通し確認のやり方: スクショを ffmpeg で動画にして `--test-input` で流し、プレーヤーのウィンドウに PostMessage でキーを送り、PrintWindow(PW_RENDERFULLCONTENT) で撮る。
  **PowerShell 5.1 のスクリプトは BOM 付き UTF-8 で保存すること**（BOM なしだと日本語のコメントで次の行が消える）。撮影側は SetProcessDPIAware しないと右側が切れる。
  静止画を concat demuxer でつなぐと最初の画像が落ちるので、`-loop 1 -t N -i` を concat フィルタでつなぐ。

## 開始時の処理（StartSession）
1. エンコーダ: 自動なら h264_nvenc → h264_qsv → h264_amf → libx264 の順に lavfi で試し、結果を settings.ini にキャッシュ
2. デバイス確認: `-t 2 -f null -` で開けるか試す。だめなら (a) 解像度/fps 指定を外す (b) 音声なしにする（ユーザーに確認）の順で緩める
3. PlayerForm を表示 → mpv（--wid）→ IPC 接続 → ffmpeg。mpv が閉じたら ffmpeg を Kill
- キャッシュ容量 = (映像kbps + 220) * 1000/8 * 秒数 * 1.2 を back/forward 両方に設定

## ハマりどころ・判明していること
- **ffmpeg master では `-thread_queue_size` を入力オプションに使えない**。
- **h264_amf は `-g` だけではキーフレームを入れない**（最初の1枚だけ → 途中から再生できず「キャプチャ開始中...」のまま、シークもできない）。`-force_key_frames expr:gte(t,n_forced*1) -forced_idr 1 -header_spacing N` が必要。
- **名前付きパイプへの ffmpeg 出力は `-y` が要る**（既存ファイル扱いで "File exists" で終了する）。
- **mpv のシークは seekable-ranges の終端（キーフレーム単位）を超えてはいけない**。lua の `cache_range()` の第3値でクランプする。
- ライブ時は遅れ > 1.2s で speed 1.08、< 0.45s で 1.0 に戻す。連打は直前のシーク先 `pending` から積み上げる。
- **ペン（lua）**: 線は映像基準の 0〜1 座標。描画は「行って戻る」面積ゼロの ASS 図形の縁取り（\bord）。`click` は complex バインドで down/up を受ける。パレットは画面中央（2つの盤面の間、NEXT 欄の下・勝利数/セット数の上）に2段。位置は映像の 1920×1080 基準。6色。
- **mpv の `window-dragging` が有効だと左ドラッグの途中で up が飛ぶ** → `window-dragging=no`。
- mpv の標準キー割り当ては `input-builtin-bindings=no` で無効化（`CLOSE_WIN quit` を忘れない）。
- --wid で埋め込んだ mpv の上に WinForms のコントロールを重ねても、正しく前面に描かれる（d3d11 でも可）。
- **Windows で --wid 埋め込みの mpv はマウスを受け取らない**（カーソルの下はアプリ側のパネル。mouse-pos が 0,0 / hover=false のまま）。PlayerForm の VideoHost でマウスの移動・ボタン・ホイールを受け、IPC の `mouse x y` / `keydown`・`keyup MBTN_*` / `keypress WHEEL_*` で送る。ダブルクリックは mpv が押す間隔から判定する。カーソルは隠さない（ユーザーの希望）。
- WinForms の UserControl はウィンドウに追加した時点でハンドルが作られるので、「表示済みか」は自前のフラグで管理する。
- 解析の時刻: 解析用画像の到着時刻を、定期取得した demuxer-cache-time に換算する。対応が取れる前の数秒は最初の画像からの経過時間で代用。
- 認識: 3列目12段目の × 印は赤ぷよと紛らわしい（埋まり具合 55% 未満は空）。ゴーの瞬間は画面が少し暗く、★の金色は彩度・明るさを緩めて判定。セット数の文字は細く圧縮で途切れるので、数字範囲の暗い画素をまとめて1つの形として比べる。勝利数・セット数は同じ読みが3枚続いたものだけ使う。
- ツモ: NEXT 欄は上が子・下が軸と仮定。識別で一致しなければ軸子を入れ替えて試す。ゴーの時点の NEXT/NEXT2 を1・2手目とする（**実機の映像では未確認**）。
- **OBS 起動中に開始すると「キャプチャ開始中...」のまま真っ暗**だった（OBS を閉じたら映った）。ドライバの同時利用の挙動は未調査。
- 音声: 以前は「GV-4K60 の音声が取れない」としていたが、settings.ini では音声に `GV-4K60_PCIE, Audio 01 Capture`（device モード）を指定しており、ログ上も音声ストリームが来ている。解決済みの可能性が高い。

## 今後の候補
- 実機の対戦映像での検知・ツモ読み取りの確認と調整（特に NEXT の向き、ゴー時点の手の番号、連続して同じ組が来る場合）
- 巻き戻し中の 1.5x / 2x 再生、クリップ書き出し、音量の保存
