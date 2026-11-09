// ETD試作のスキーマ。
// items: 永続所持品（敗北でも消えない）
// runs: 潜行（進行中は最大1件。result が入ったものが記録）
// events: 階ごとの出来事ダイジェスト＋彼女の文章

import { integer, real, sqliteTable, text } from "drizzle-orm/sqlite-core";

export const items = sqliteTable("items", {
  id: integer("id").primaryKey({ autoIncrement: true }),
  name: text("name").notNull(),
  system: text("system").notNull(), // 系統キー A〜S
  bonus: integer("bonus").notNull(), // 回避ボーナス
  rarity: integer("rarity").notNull(), // 1〜5
  createdAt: integer("created_at", { mode: "timestamp_ms" })
    .notNull()
    .$defaultFn(() => new Date()),
});

export const runs = sqliteTable("runs", {
  id: integer("id").primaryKey({ autoIncrement: true }),
  course: integer("course").notNull(), // 10 / 20 / 30
  difficulty: text("difficulty", {
    enum: ["easy", "normal", "hard"],
  }).notNull(),
  // equip=装備選択中 / dive=潜行中 / await_text=文章待ち / finished=終了
  phase: text("phase", {
    enum: ["equip", "dive", "await_text", "finished"],
  }).notNull(),
  floor: integer("floor").notNull().default(0),
  returnFloor: integer("return_floor").notNull(),
  arousal: real("arousal").notNull().default(0),
  sensitivity: real("sensitivity").notNull().default(1.0),
  climaxes: integer("climaxes").notNull().default(0),
  clothes: text("clothes", { enum: ["正常", "損傷", "大破"] })
    .notNull()
    .default("正常"),
  effectsJson: text("effects_json").notNull().default("[]"),
  equippedIdsJson: text("equipped_ids_json").notNull().default("[]"),
  pendingItemsJson: text("pending_items_json").notNull().default("[]"),
  pendingFinish: text("pending_finish", { enum: ["clear", "defeat"] }),
  result: text("result", { enum: ["クリア", "生還", "敗北", "中断"] }),
  gainedJson: text("gained_json").notNull().default("[]"),
  lostJson: text("lost_json").notNull().default("[]"),
  startedAt: integer("started_at", { mode: "timestamp_ms" })
    .notNull()
    .$defaultFn(() => new Date()),
  finishedAt: integer("finished_at", { mode: "timestamp_ms" }),
});

export const events = sqliteTable("events", {
  id: integer("id").primaryKey({ autoIncrement: true }),
  runId: integer("run_id").notNull(),
  floor: integer("floor").notNull(),
  digestJson: text("digest_json").notNull(),
  text: text("text"), // 彼女の文章（submit_text で保存。マーカー行は除去済み）
  marksJson: text("marks_json").notNull().default("[]"), // 文章内タイミングマーカー [{atLine, type}]
  // 罠シーンの画像スロット：所属はこのイベント行そのもの、タイミングは marks の [[trap]] が担う。
  // imagePromptJson: 生成リクエストのスナップショット {positive, negative, sceneExcerpt, basis}（NAI形式タグ）
  imagePromptJson: text("image_prompt_json"),
  imageBlobKey: text("image_blob_key"), // 生成済み画像の ctx.blobs キー
  // none=リクエスト未作成 / awaiting=プロンプト保存済み・未生成 / generating=生成中 / ready=生成済み / failed=生成失敗
  imageStatus: text("image_status", {
    enum: ["none", "awaiting", "generating", "ready", "failed"],
  })
    .notNull()
    .default("none"),
  createdAt: integer("created_at", { mode: "timestamp_ms" })
    .notNull()
    .$defaultFn(() => new Date()),
});
