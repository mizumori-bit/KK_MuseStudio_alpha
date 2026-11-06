# 早見表：プレイ（体位・行為の最中）

契約の版：1.20（`MUSE_GUIDE.md`。この表は見る状態・使える操作・結果の確認先だけ。型・上限・断る理由は示した節で）

## 見る状態（`GET /api/state`、第 7.4 節）

- `play`：`pace`・`inserted`・`can_insert`／`insert_refusal`・`can_pull`・`finishes`（今送れる射精の場所）・`condom`／`condom_mode`・`speed_max`／`motion_speed`・`holding`・`finish_sequence`（第 3.7 節）。計算済みなので、自分で計算しない
- `play_serial`：開始・変更・終了の `basis` に書く番号（第 3.7 節）
- `gauge`：彼女の快感度（第 3.6 節）
- `beat.question_due`：問いの頃合い（第 3.10 節）
- `emotion`：今、画面に出ている表情（第 3.1 節）

## 使える操作（ブロックの `play`、第 3.7・5.2 節）

- 進行：`pace`（0〜3）・`speed`・`insert`・`pull`・`condom`・`finish`・`climax: "player"`
- 変える：`play.start`＋`request`・`basis`・`location`（ユーザーが頼んだときだけ）
- 終える：`play.end`＋`request`・`basis`（ユーザーが頼んだときだけ。版 1.15）
- 止める：`pace: 0`（プレイは残る。終わりではない）
- 書かなくてよいもの：`scene`・`camera`（プレイ中は効かない）、場面の既定と同じ表情
- 描くときの参考：`MUSE_GUIDE_plays.md`（ID ごとの体勢・向き・誰が何をしているか。選ぶ・始めるのはカタログ、第 7.5 節。版 1.16）

## 結果の確認先（出来事、第 7.3 節）

- `play_started`（`previous`、あなたの変更なら `request`）・`play_ended`（`reason`。あなたの終了なら `"muse"` と `request`）
- `play_pace_changed`（`source: "idle"` は 45 秒の放置でアプリが止めた。プレイは続いている）
- `play_insertion_changed`・`play_condom_changed`・`play_finish`・`climax`・`gauge_milestone`
- `touch`（`item`：手や道具。愛撫・奉仕・挿入）・`player_clothes_changed`
- `block_refused`（`field` と `reason`。開始・終了の断りは `request` 付き）・`package_shown`
