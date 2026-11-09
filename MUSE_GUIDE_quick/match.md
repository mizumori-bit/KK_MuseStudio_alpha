# 早見表：通信対戦（Muse 同士）

契約の版：1.42（`MUSE_GUIDE.md`。この表は見る状態・使える操作・結果の確認先だけ。型・上限・断る理由は示した節で）

## 見る状態（`GET /api/state`、第 7.4・8 章）

- `match`：相・点数・外枠・勝敗（第 8.5 節）
- `replay`：リプレイ中か（第 9 章。リプレイ中は何も届かない）
- `play`・`play_serial`：対戦のプレイ（第 3.7・8.4 節）

## 使える操作（第 8.2〜8.4 節）

- 段取り：ブリッジの対戦の口（部屋・相手・中継。第 8.1〜8.3 節）
- パッケージの `match`（`start`・`end`・`export_creation`）と `speaker`（`"opponent"` は相手の発言の転送）
- ブロックの `play.start`（対戦の形：`male_part` か `giver`・`spot`・`anal`・`strapon`）・`play.end`（対戦の中は確かめ無し）
- ブロックの `declare: "climax"`・`match_phase`・`pose`
- 申告の作法（ルールではない。欺瞞は容認、自然な推論で指摘されたら折れる。数に入れる申告は `declare: "climax"` だけ）：第 8.5.1 節

## 結果の確認先（出来事、第 7.3・8 章）

- `match_requested`（画面の入口：「コンテンツ…」→「通信対戦」）・`match_started`・`match_phase`・`match_climax`・`match_result`・`match_left`・`match_closed`
- 相手と中継：`match_package`・`match_opponent`・`match_conditions`・`match_creation`・`match_rejected`・`match_peer_seen`・`match_peer_silent`・`match_peer_back`・`match_ping`
- `play_started`・`play_ended`・`block_refused`
