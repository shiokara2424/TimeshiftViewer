# CLAUDE.md — Timeshift Viewer 開発メモ

## 目的
キャプチャボード（ユーザー環境: I-O DATA GV-4K60/PCIE、DirectShow 名 `GV-4K60_PCIE, Video 01 Capture`）の映像を
ライブ表示しつつ、直近 N 分（既定10分）を巻き戻し・一時停止・コマ送りできるようにする。古いデータは捨てる。

## 構成
```
TimeshiftViewer.exe (C#, .NET Framework 4.8, WinForms)
  ├ 設定画面・初回ダウンロード・プロセス管理
  ├ ffmpeg.exe : DirectShow で取り込み → H.264(GPU)+AAC → MPEG-TS を stdout へ
  │     ↓ (C# が stdout → mpv stdin に CopyToAsync で中継)
  └ mpv.exe   : stdin を再生。demuxer キャッシュ（back/forward）が巻き戻しバッファ
        └ scripts/timeshift.lua : OSD(バッジ/シークバー/ボタン)、連打の積み上げ、ライブ追従
```

| ファイル | 内容 |
|---|---|
| `Core.cs` | `Paths`（保存先。exe 横に書けなければ %LOCALAPPDATA%）、`AppSettings`（settings.ini, key=value）、`ToolInstaller`（ffmpeg: BtbN の GitHub latest zip / mpv: shinchiro の GitHub release .7z を 7zr.exe で展開）、`FF`（ffmpeg 実行・dshow デバイス列挙・エンコーダ検出・引数生成） |
| `MainForm.cs` | 設定画面、セッション開始（エンコーダ決定→デバイス確認→起動）、`RunPlayer`（ffmpeg→mpv 中継と終了監視）、`Program.Main` |
| `mpvconf/mpv.conf` `input.conf` `timeshift.lua` | exe に EmbeddedResource として埋め込み、起動時に `mpv-config/`（lua は `mpv-config/scripts/`）へ書き出す。mpv は `--config-dir` でこれを読む |
| `Puyo/Tsumo.cs` | ぷよぷよeスポーツ（対戦）のツモ生成とシード値の識別。仕様はユーザー提供の puyo_tsumo_spec.md（乱数 x=x*0x5D588B65+0x269EC3、色決め5回、16/32/64 ブロック入れ替え、開幕2手は3色で上書き）。識別は先頭16手の表（65536×32×3 byte、作成 30ms 程度）を総当たり |
| `Puyo/Field.cs` | 盤面（6×14、y=0 が下、y=12 が13段目＝連鎖で消えない、y=13 に来たぷよは消える）、落下・連鎖・通ルールの得点 |
| `Puyo/SimState.cs` | シミュレーターの局面ツリー（Undo/Redo/分岐）とツモの出どころ。画面から独立 |
| `Puyo/PuyoViews.cs` `SimForm.cs` `TsumoDialog.cs` | シミュレーター画面。設定画面のボタン、mpv の T キー／「シミュ」ボタン（script-message tsv-sim を IPC で受信）、`TimeshiftViewer.exe --sim [--tsumo=4:21926]` で開く |
| `Tests/` | Puyo/ のロジックを確認するコンソール（`dotnet run --project Tests -c Release`）。仕様書の正解データ・識別テスト・連鎖・履歴 |
| `legacy/browser-version.html` | 最初に作ったブラウザ版（WebCodecs）。GV-4K60 の音声がブラウザから見えないため Windows アプリ版に移行した。参考用 |

## ビルドと確認
- Windows: `dotnet build -c Release`（ビルド後に exe をこのフォルダ直下へコピーする。直下が実行用フォルダ＝tools / settings.ini / mpv-config の置き場。起動中だとコピーは警告になり失敗する）
- `samples/` は盤面認識の調整用スクリーンショット（ぷよぷよeスポーツ、1080p）
- Windows で mpv 単体を確認する方法: ffmpeg の lavfi テスト映像を `-f mpegts -` で mpv にパイプし、`--config-dir=mpvconf のコピー --input-ipc-server=\\.\pipe\xxx --title=別名 --geometry=960x540`。IPC の `mouse x y` / `keydown MBTN_LEFT` / `keyup` でドラッグを再現、`screenshot-to-file <path> window` で OSD 込みの画像を取る（`--vo=image` は OSD が描かれないので不可）
- Linux（開発時に使った方法）: mono-devel を入れて `dotnet build -c Release`（csproj が OS を見て `/usr/lib/mono/4.8-api` を参照する）
- Linux での動作確認: `xvfb-run` + Mono で exe を起動。`tools/ffmpeg.exe` `tools/mpv.exe` を、Linux の ffmpeg（lavfi テスト映像）と mpv を呼ぶシェルスクリプトに差し替えて通しで確認した。mpv は `--input-ipc-server` で状態（time-pos, demuxer-cache-state）を取得、ボタンは xdotool でクリック確認。

## 開始時の処理（StartSession）
1. エンコーダ: 自動なら h264_nvenc → h264_qsv → h264_amf → libx264 の順に lavfi で試し、結果を settings.ini にキャッシュ
2. デバイス確認: `-t 2 -f null -` で開けるか試す。だめなら (a) 解像度/fps 指定を外す (b) 音声なしにする（ユーザーに確認）の順で緩める
3. mpv を起動 → ffmpeg を起動 → stdout を mpv stdin へ中継。mpv が閉じたら ffmpeg を Kill。mpv 終了時点で ffmpeg が既に終了していれば異常停止としてログを表示
- キャッシュ容量 = (映像kbps + 220) * 1000/8 * 秒数 * 1.2 を back/forward 両方に設定（一時停止中は forward が溜まるため）

## ハマりどころ・判明していること
- **ffmpeg master では `-thread_queue_size` を入力オプションに使えない**（エラーになる）。削除済み。
- **mpv のシークは seekable-ranges の終端（キーフレーム単位）を超えてはいけない**。`demuxer-cache-time` まで飛ばすとキャッシュ外扱いになり、"Cannot seek backward in linear streams" でキャッシュが分断される。lua の `cache_range()` は (先頭, 表示用の最新時刻, シーク可能終端) を返し、シークは必ず第3値でクランプする。
- ライブ時は遅延が溜まらないよう、遅れ > 1.2s で speed 1.08、< 0.45s で 1.0 に戻す。
- 連打: 直前のシーク先 `pending` から 1 秒以内なら、それを基準に積み上げる（time-pos はシーク完了まで更新されないため）。ボタン上のダブルクリックは全画面切替にしない。
- 一時停止中に保持時間を超えたら、再生位置を前へ送って古いデータを捨てる。
- **ペン（lua）**: 線は映像基準の 0〜1 座標で保持（osd-dimensions の余白を除いて換算）。描画は「行って戻る」面積ゼロの ASS 図形の縁取り（\bord）で線にする。`click` は complex バインドで down/up を受ける。
- **mpv の `window-dragging` が有効だと、左ドラッグの途中で up が飛んでペンが1点で切れる**。mpv.conf で `window-dragging=no`。
- mpv の標準キー割り当ては `input-builtin-bindings=no` で無効化。input.conf に書いたキーだけが有効（`CLOSE_WIN quit` を忘れると × で閉じられない）。
- Paths.ExtractMpvConfig は起動直後（OnFirstShown）にしか呼ばれない。起動中に mpv-config を消すと次のセッションが設定なしで動く。
- **OBS 起動中に開始すると「キャプチャ開始中...」のまま真っ暗**だった（OBS を閉じたら映った）。ドライバの同時利用の挙動は未調査。
- **GV-4K60 の音声は未対応（要対応・最優先）**:
  - Windows の録音デバイス（WASAPI）には出てこない。
  - ffmpeg で `video=X:audio=X` を指定すると "Could not find output pin from audio only capture device" → 映像フィルタ自体に音声ピンがない。
  - OBS は取れている。OBS（libdshowcapture）は映像デバイスと同じデバイスパスを持つ音声フィルタ（KSCATEGORY_AUDIO / AM_KSCATEGORY_CAPTURE 等）を探して組み合わせていると思われる。ffmpeg の dshow は CLSID_AudioInputDeviceCategory しか探さない。
  - 対応案: C# から DirectShow COM（または DirectShowLib）で対応する音声フィルタを探して PCM を取得し、名前付きパイプ経由で ffmpeg の第2入力（`-f s16le -ar 48000 -ac 2 -i \\.\pipe\...`）に渡す。A/V 同期の扱いに注意。
  - まず確認すべきこと: 設定画面の音声一覧（ffmpeg の dshow audio 列挙）に GV 関連の項目が出るか、OBS のソース設定で「カスタム音声デバイスを使用する」がオンか（オンならそのデバイス名を ffmpeg で指定すれば済む）。

## 今後の候補
- 音声対応（上記）
- 巻き戻し中の 1.5x / 2x 再生（ライブに追いつく用）
- 選択範囲をファイルに保存（クリップ書き出し）
- 音量の保存（現在は settings.ini の Volume 固定値を渡すだけ）
