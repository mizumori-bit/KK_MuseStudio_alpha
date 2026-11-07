# 早見表：会話（プレイの外・彼女と一緒）

契約の版：1.29（`MUSE_GUIDE.md`。この表は見る状態・使える操作・結果の確認先だけ。型・上限・断る理由は示した節で）

## 見る状態（`GET /api/state`、第 7.4 節）

- `location`・`her_location`・`presence`・`belief`：どこにいて、一緒か（第 3.4・3.12 節）
- `emotion`：今、画面に出ている表情。`face`：あなたが付けて保っている表情（`face.muse`、次の表情か場面の切り替えまで。安全時間 300 秒）と、割り込んでいる一瞬の表情（心の判断には使わない。第 3.1 節・7.4 節）
- `mood`・`urges`：気分のゲージと、まだ応えていない欲求（第 3.9 節）
- `beat`：`since_presented`・`question_due`（問いの頃合い。第 3.10 節）
- `relationship`：最後に受け取った関係値（読むときは `person` を確かめる。第 3.11 節）
- `settings`：快感度・発情の上がる速さ（第 7.4 節）
- `play_serial`：プレイの開始・変更に使う番号（第 3.7 節）

## 使える操作（パッケージ、第 5 章）

- ブロック：`text`・`emotion.label`（第 3.1 節）・`scene.key`（第 3.2 節）・`camera`（第 3.3 節）・`location`（第 3.4 節）・`clothes`（第 5.2 節）・`action`（第 3.9 節）・`anim: "climax"`（第 3.6 節）・`mood`（第 3.9 節）・`belief`（第 3.12 節）
- プレイを始める：ブロックの `play.start`＋`request`・`basis`・`location`（ユーザーが頼んだときだけ。第 3.7 節）
- パッケージ：`choices`（第 5.3 節、同じ選択肢をチャットにも）・`relationship`（第 3.11 節）・`monologue`（第 3.12 節）・`settings`（ユーザーが頼んだときだけ。第 5.1 節）
- 省略してよいもの：場面の既定と同じ表情（第 3.1 節）

## 結果の確認先（出来事、第 7.3 節）

- `package_shown`（出た時刻）・`block_refused`（効かなかった項目と理由）・`package_dropped`
- 届いたか・実行されたか分からないとき：`GET /api/packages/status?id=<送った id>`（`started` なら実行済み。送り直す前に。第 7.1 節。版 1.17）
- `touch`（`reaction` の `level` は反応の段階。触った回数でも関係の段階でもない。第 3.6 節）
- `clothes_changed`・`location_changed`・`together_changed`・`presence_changed`
- `urge`・`urge_cleared`・`mood_changed`・`mood_applied`
- `play_started`（あなたの開始なら `source: "muse"` と `request`）
- `settings_changed`・`character_changed`
