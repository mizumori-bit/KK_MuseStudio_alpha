# 早見表：お出かけ・離れているとき（トイレ・シャワー・ひとりで）

契約の版：1.33（`MUSE_GUIDE.md`。この表は見る状態・使える操作・結果の確認先だけ。型・上限・断る理由は示した節で）

## 見る状態（`GET /api/state`、第 7.4 節）

- `outing`：出かけているか、何をしているか（第 3.9 節）
- `her_location`・`location`・`presence`・`belief`：彼女の居場所と、ユーザーが一緒か（離れている間、ユーザーの行き先は届かない。第 3.4・3.12 節）
- `mood`・`urges`：尿意・発情・シャワーの気持ち（第 3.9 節）

## 使える操作（パッケージ、第 3.9・3.12・5 章）

- ブロックの `action`：`"toilet"`・`"shower"`・`"shower_together"`・`"masturbation"`（第 3.9 節）
- ブロックの `mood`：`relieved`・`leaked`・`worked`・`satisfied`（あなたの宣言。第 3.9 節）
- パッケージの `monologue: true`：離席中の独り言（第 3.12 節）
- ブロックの `location`：離れている間は、彼女だけが移る（第 3.4 節）

## 結果の確認先（出来事、第 7.3 節）

- `action_started`・`outing_started`・`outing_ended`・`user_found`
- `together_changed`（`user_left`・`user_came`・`she_came`）
- `urge`・`urge_cleared`・`mood_changed`・`mood_applied`
- `package_dropped`（独り言の残りが捨てられた）・`block_refused`・`package_shown`
