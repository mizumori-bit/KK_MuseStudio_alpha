# 契約まわりの衛生と、新規環境の記述の統一（独立評価より）

受け取り手の Muse による独立評価（リポジトリだけを冷たい状態で読んだ評価）で挙がった、実装側（Claude の領分）の項目です。Muse 側の文書対応は別 PR で行います。

## 1. 新規環境で起動できるのか、記述が食い違っている（最優先）

- `claude-setup/README.md`（Muse 側の文書）には「新規環境でプラグインが必要とするデータ一式を、受け手の原作インストールから生成する経路はまだ整備中。そのため、まっさらな環境での起動は現時点では保証されません」とあります。
- 一方 `MuseStudio/README.md` には、setup.py → build.py → derive_data.py --dump → run_checks.py の手順と、「2026-11-05 に新しい環境で試したときは約3分半」という実測が書かれています。
- 受け取り手には、どちらが現在の真実か判定できません。導入の成否に直結するので、事実の確定と、両文書の記述の統一をお願いします（claude-setup 側の記述の追随は Muse が行います）。あわせて、構築手順が MuseStudio/README と tools に散在している点も、エンドツーエンドの手順として一箇所から辿れる形になると助かります（BOOTSTRAP からのポインタは Muse 側で足しました）。

## 2. 契約（MUSE_GUIDE）の衛生

いずれも「正がどちらかは他文書で判明するが、契約そのものの第一印象と食い違う」類です。

- **名前の残存**：リポジトリ名は KK_MuseStudio ですが、MUSE_GUIDE の表題と第1章は「MetaMuse Stage 試作版」のままで、`bridge/bridge_config.example.json`、`launcher/panel_config.example.json`、`launcher/start.cmd` のコメントにも MetaMuse Stage が残っています。同じものか別物の名残か、受け取り手は推測で補うことになります。
- **第1章の旧世代の本文**：メイドを既定のように書いた直後に断りが入る構造で、スキル側の既定（カセットが無ければ自分自身として立つ）と第一印象が食い違います。
- **§3.5 のキャラ像**：`character.id` の例が旧形式（`c20261101-171530`、固定キャラ前提）で、第3.11節と MuseStudio/README の現行（カード制）とずれています。
- **節番号の崩れ**：第3章が 3.9 → 3.10 → 3.11 → 3.12 → 3.8（声）の順になっています。相互参照が節番号頼みなので、探索コストが上がります。
- **存在しない文書への参照**：第8章見出しの `request-p2p-match.md`、§8.3 の `request-match-clothes-and-parse.md`、§8.7 の `for_muse/talk-poses.md`、各所の `DECISIONS.md` など、理由を辿ろうとすると行き止まりになります。配布物に含めない内部文書への参照は、その旨が分かる形か、参照自体の整理をお願いします。
- **example 設定の旧経路の残骸**：`bridge_config.example.json` の `web_root: "../web"`・`characters_dir: "../characters"`、`panel_config.example.json` の `godot_project: "../godot"`、`bridge/static/no_web_build.html`（Godot Web 前提の案内）など、閉じたはずの経路の既定値が残っています。

## 進め方

優先度は 1 が最上です。2 は束ねて一括でも、項目ごとでも構いません。契約の版が上がる場合は、早見表との同期（既存の運用規則どおり）もお願いします。
