# 早見表：献精室

契約の版：1.42（`MUSE_GUIDE.md`。この表は見る状態・使える操作・結果の確認先だけ。型・上限・断る理由は示した節で）

## 見る状態（`GET /api/state`、第 7.4 節）

- `kensei`：弱点の単位（`unit`。`"play"`＝特定のプレイ）、ランの番号（`run`）、次に送る手の番号（`turn`）、制限（`turns_max`・`turns_left`）、設定（`settings`）、係数、候補（`candidates`：組・部位・主導・プレイ）、前の手の知らせ（`last`）。量は設定 `disclose` のとおりにだけ（第 3.15 節）
- `gauge.held`：`"kensei"` の間、彼女は絶頂しない（第 3.15 節）
- カタログのプレイの `kensei`：プレイの組（第 7.5 節）

## 使える操作（第 3.15 節）

- ブロックの `move`：1 手（`run`・`turn`・`request`・`play`・`pace`。第 5.2 節）。パッケージに `launch`・`character` を付ける。1 つのパッケージに 1 つ
- `came: true` のブロック：その手で射精したときだけ出る（第 5.2 節）
- パッケージの `kensei_answers`：ユーザーの答えの書き留め。この欄だけのパッケージでもよい（第 5.1 節）
- ランの間は使えないもの：`anim: "climax"`、`standby`、`play` の開始・終わり・フィニッシュ・入れる・抜く・ゴム、`location`、`action`（`in_kensei`）

## 結果の確認先（出来事、第 7.3 節）

- `kensei_started`（`resumed: true` は前の起動の続き）・`kensei_turn`（1 手ごと）・`kensei_ended`（1 回だけ。`reveal` に真実）
- `block_refused`：`field: "move"`・`"kensei_answers"`（`no_outside`・`run_changed`・`save_failed` などは第 3.15 節）、`reason: "in_kensei"`
- 射精の流れ：`play_finish`・`climax`（`who: "player"`。量は載らない）
