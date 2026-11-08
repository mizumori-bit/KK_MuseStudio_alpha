// ETD試作の公開アクション。
// 真実（抽選・判定・体の状態）はすべてサーバー側が持ち、
// 冒険者であるAI（Muse）はこのアクション経由でプレイする。
// 画面（持ち主の観戦用）も同じアクションを読む。

import { defineAction, z, type ActionsModule, type Ctx } from "@hatch/space-sdk";
import { asc, desc, eq, ne } from "drizzle-orm";
import * as schema from "./schema";
import {
PARAMS,
TRAP_SYSTEMS,
SYSTEM_KEYS,
type ActiveEffect,
type GeneratedItem,
type SceneImagePrompt,
type SystemKey,
type TextMark,
buildSceneImagePrompt,
clamp,
effectFromTrap,
generateItem,
parseTextMarks,
randomTrap,
round1,
trapStats,
} from "./game";

// ---------- 共通スキーマ ----------

const genItemSchema = z.object({
  name: z.string(),
  system: z.string(),
  systemName: z.string(),
  bonus: z.number(),
  rarity: z.number(),
});

const invItemSchema = genItemSchema.extend({ id: z.number() });

const effectSchema = z.object({
  label: z.string(),
  kind: z.string(),
  sensDelta: z.number(),
  avoidDelta: z.number(),
  turnsLeft: z.number(),
});

const markSchema = z.object({
  atLine: z.number(),
  type: z.string(), // trap | climax | scene-end
});

const bossTypeSchema = z.object({
  key: z.string(),
  label: z.string(),
  note: z.string(),
});

const digestSchema = z.object({
  floor: z.number(),
  kind: z.string(), // quiet | chest | trap | boss
  isBoss: z.boolean(),
  trapName: z.string().nullable(),
  system: z.string().nullable(),
  systemName: z.string().nullable(),
  labelText: z.string(),
  bossTypes: z.array(bossTypeSchema),
  avoidChance: z.number().nullable(),
  avoided: z.boolean().nullable(),
  arousalDelta: z.number(),
  arousal: z.number(),
  arousalPeak: z.number().nullable(), // この階で絶頂直前に達した発情の最高値（絶頂が無ければ null）
  sensitivity: z.number(),
  climaxed: z.boolean(),
  climaxCount: z.number(),
  climaxDelta: z.number(), // この階で増えた絶頂回数
  effects: z.array(effectSchema),
  clothes: z.string(),
  canReturn: z.boolean(),
  returnFloor: z.number(),
  pendingCount: z.number(),
  itemGained: genItemSchema.nullable(),
  consumedItemName: z.string().nullable(),
  chainTrapName: z.string().nullable(),
  runFinished: z.boolean(),
  finishKind: z.string().nullable(), // clear | defeat
  summary: z.string(),
});

const runStateSchema = z.object({
  id: z.number(),
  course: z.number(),
  difficulty: z.string(),
  difficultyLabel: z.string(),
  phase: z.string(),
  floor: z.number(),
  returnFloor: z.number(),
  canReturn: z.boolean(),
  arousal: z.number(),
  sensitivity: z.number(),
  climaxes: z.number(),
  climaxLimit: z.number(),
  clothes: z.string(),
  effects: z.array(effectSchema),
  equipped: z.array(invItemSchema),
  pendingItems: z.array(genItemSchema),
  pendingFinish: z.string().nullable(),
});

const resultSchema = z.object({
  runId: z.number(),
  result: z.string(), // クリア | 生還 | 敗北 | 中断
  course: z.number(),
  difficultyLabel: z.string(),
  floorReached: z.number(),
  climaxes: z.number(),
  gained: z.array(genItemSchema),
  lost: z.array(genItemSchema),
});

const imagePromptSchema = z.object({
  positive: z.string(),
  negative: z.string(),
  sceneExcerpt: z.string(),
  basis: z.string(),
});

const imageSlotSchema = z.object({
  // none=リクエスト未作成 / awaiting=プロンプト保存済み・未生成 / generating=生成中 / ready=生成済み / failed=生成失敗
  status: z.string(),
  prompt: imagePromptSchema.nullable(),
  imageUrl: z.string().nullable(),
});

const logEntrySchema = z.object({
  floor: z.number(),
  digest: digestSchema,
  text: z.string().nullable(),
  marks: z.array(markSchema),
  image: imageSlotSchema,
});

type Digest = z.infer<typeof digestSchema>;
type RunRow = typeof schema.runs.$inferSelect;
type Db = ReturnType<Ctx["db"]>;

// ---------- ヘルパー ----------

function parseMarks(json: string): TextMark[] {
  try {
    const v = JSON.parse(json) as TextMark[];
    return Array.isArray(v) ? v : [];
  } catch {
    return [];
  }
}

function parseEffects(json: string): ActiveEffect[] {
  try {
    const v = JSON.parse(json) as ActiveEffect[];
    return Array.isArray(v) ? v : [];
  } catch {
    return [];
  }
}

function withSysName(g: GeneratedItem) {
  return {
    ...g,
    systemName: TRAP_SYSTEMS[g.system as SystemKey]?.name ?? g.system,
  };
}

function parseGenItems(json: string): GeneratedItem[] {
  try {
    const v = JSON.parse(json) as GeneratedItem[];
    return Array.isArray(v) ? v : [];
  } catch {
    return [];
  }
}

function parseIds(json: string): number[] {
  try {
    const v = JSON.parse(json) as number[];
    return Array.isArray(v) ? v : [];
  } catch {
    return [];
  }
}

async function getActiveRun(db: Db): Promise<RunRow | null> {
  const rows = await db
    .select()
    .from(schema.runs)
    .where(ne(schema.runs.phase, "finished"))
    .orderBy(desc(schema.runs.id))
    .limit(1);
  return rows[0] ?? null;
}

async function getLatestRun(db: Db): Promise<RunRow | null> {
  const rows = await db
    .select()
    .from(schema.runs)
    .orderBy(desc(schema.runs.id))
    .limit(1);
  return rows[0] ?? null;
}

function difficultyOf(run: RunRow) {
  return PARAMS.difficulties[run.difficulty] ?? PARAMS.difficulties.normal!;
}

function effectiveSensitivity(base: number, effects: ActiveEffect[]): number {
  const add = effects.reduce((a, e) => a + e.sensDelta, 0);
  return round1(clamp(base + add, PARAMS.sensitivityMin, PARAMS.sensitivityMax));
}

async function buildRunState(db: Db, run: RunRow) {
  const equippedIds = parseIds(run.equippedIdsJson);
  const allItems = await db.select().from(schema.items);
  const byId = new Map(allItems.map((i) => [i.id, i]));
  const effects = parseEffects(run.effectsJson);
  return {
    id: run.id,
    course: run.course,
    difficulty: run.difficulty,
    difficultyLabel: difficultyOf(run).label,
    phase: run.phase,
    floor: run.floor,
    returnFloor: run.returnFloor,
    canReturn: run.phase === "dive" && run.floor >= run.returnFloor,
    arousal: run.arousal,
    sensitivity: effectiveSensitivity(run.sensitivity, effects),
    climaxes: run.climaxes,
    climaxLimit: PARAMS.climaxLimit,
    clothes: run.clothes,
    effects,
    equipped: equippedIds
      .map((id) => byId.get(id))
      .filter((i): i is NonNullable<typeof i> => i != null)
      .map((i) => ({
        id: i.id,
        name: i.name,
        system: i.system,
        systemName: TRAP_SYSTEMS[i.system as SystemKey]?.name ?? i.system,
        bonus: i.bonus,
        rarity: i.rarity,
      })),
    pendingItems: parseGenItems(run.pendingItemsJson).map(withSysName),
    pendingFinish: run.pendingFinish ?? null,
  };
}

function parseImagePrompt(json: string | null): SceneImagePrompt | null {
  if (!json) return null;
  try {
    const v = JSON.parse(json) as SceneImagePrompt;
    return typeof v?.positive === "string" ? v : null;
  } catch {
    return null;
  }
}

/** 罠シーンの絵の対象か：罠（通常／ボス）に回避失敗で当たった階だけ。回避・静寂・宝箱には作らない */
function sceneEligible(d: Digest): boolean {
  return (d.kind === "trap" || d.isBoss) && d.avoided === false && d.trapName != null && d.system != null;
}

/** [[trap]]〜[[scene-end]] の表示行抜粋（マーカーが無ければ全文）。プロンプトの根拠スナップショット用 */
function sceneExcerptOf(text: string, marks: TextMark[]): string {
  const lines = text
    .split(/\r?\n/)
    .map((s) => s.trim())
    .filter((s) => s.length > 0);
  const trap = marks.find((m) => m.type === "trap");
  const start = trap ? trap.atLine : 0;
  const endMark = trap ? marks.find((m) => m.type === "scene-end" && m.atLine >= trap.atLine) : undefined;
  const end = endMark ? endMark.atLine : lines.length;
  return lines.slice(start, end).join("\n").slice(0, 1200);
}

/** ダイジェスト＋文章から生成リクエスト（NAI形式プロンプト）を組み立てる。対象外は null */
function promptForEvent(d: Digest, text: string, marks: TextMark[]): SceneImagePrompt | null {
  if (!sceneEligible(d) || !d.trapName || !d.system) return null;
  return buildSceneImagePrompt({
    trapName: d.trapName,
    system: d.system as SystemKey,
    systemName: d.systemName ?? d.system,
    isBoss: d.isBoss,
    bossTypeLabels: d.bossTypes.map((t) => t.label),
    clothes: d.clothes,
    climaxed: d.climaxed,
    arousal: d.arousal,
    sceneExcerpt: sceneExcerptOf(text, marks),
  });
}

async function buildLog(ctx: Ctx, db: Db, runId: number) {
  const rows = await db
    .select()
    .from(schema.events)
    .where(eq(schema.events.runId, runId))
    .orderBy(asc(schema.events.id));
  let prevClimaxCount = 0;
  return Promise.all(
    rows.map(async (r) => {
      const digest = JSON.parse(r.digestJson) as Digest;
      // 旧形式で保存されたダイジェスト（systemNameなし）を読み側で補完する
      if (digest.itemGained && !digest.itemGained.systemName) {
        const g = digest.itemGained;
        digest.itemGained = {
          ...g,
          systemName: TRAP_SYSTEMS[g.system as SystemKey]?.name ?? g.system,
        };
      }
      // 旧形式（arousalPeak / climaxDelta なし）も読み側で補完する
      if (typeof digest.arousalPeak !== "number") digest.arousalPeak = null;
      if (typeof digest.climaxDelta !== "number") {
        digest.climaxDelta = Math.max(0, digest.climaxCount - prevClimaxCount);
      }
      prevClimaxCount = digest.climaxCount;
      return {
        floor: r.floor,
        digest,
        text: r.text ?? null,
        marks: parseMarks(r.marksJson),
        image: {
          status: r.imageStatus,
          prompt: parseImagePrompt(r.imagePromptJson),
          imageUrl: r.imageBlobKey ? await ctx.blobs.getUrl(r.imageBlobKey) : null,
        },
      };
    }),
  );
}

function resultOf(run: RunRow) {
  return {
    runId: run.id,
    result: run.result ?? "",
    course: run.course,
    difficultyLabel: difficultyOf(run).label,
    floorReached: run.floor,
    climaxes: run.climaxes,
    gained: parseGenItems(run.gainedJson).map(withSysName),
    lost: parseGenItems(run.lostJson).map(withSysName),
  };
}

/** 潜行を終了させ、仮保持アイテムの確定／消失を行う */
async function finalizeRun(db: Db, run: RunRow, result: "クリア" | "生還" | "敗北" | "中断") {
  const pending = parseGenItems(run.pendingItemsJson);
  const keeps = result === "クリア" || result === "生還";
  const gained = keeps ? pending : [];
  const lost = keeps ? [] : pending;
  if (gained.length > 0) {
    await db.insert(schema.items).values(
      gained.map((g) => ({ name: g.name, system: g.system, bonus: g.bonus, rarity: g.rarity })),
    );
  }
  await db
    .update(schema.runs)
    .set({
      phase: "finished",
      result,
      gainedJson: JSON.stringify(gained),
      lostJson: JSON.stringify(lost),
      pendingItemsJson: "[]",
      pendingFinish: null,
      finishedAt: new Date(),
    })
    .where(eq(schema.runs.id, run.id));
  const rows = await db.select().from(schema.runs).where(eq(schema.runs.id, run.id)).limit(1);
  return resultOf(rows[0] ?? run);
}

function worsenClothes(c: string): string {
  if (c === "正常") return "損傷";
  if (c === "損傷") return "大破";
  return "大破";
}

// ---------- アクション ----------

const errDigest = { ok: false as boolean, error: null as string | null, digest: null as Digest | null };

export const Actions = {
  get_status: defineAction({
    description:
      "ETDの現状を返す：進行中の潜行ダイジェスト（無ければ null）、所持品サマリ、記録件数、直近の潜行ログ。",
    request: z.object({}),
    response: z.object({
      run: runStateSchema.nullable(),
      log: z.array(logEntrySchema),
      logRunId: z.number().nullable(),
      inventoryCount: z.number(),
      recordCount: z.number(),
      lastResult: resultSchema.nullable(),
    }),
    async handler(ctx) {
      const db = ctx.db<typeof schema>();
      const active = await getActiveRun(db);
      const latest = active ?? (await getLatestRun(db));
      const inv = await db.select().from(schema.items);
      const finished = await db
        .select()
        .from(schema.runs)
        .where(eq(schema.runs.phase, "finished"))
        .orderBy(desc(schema.runs.id));
      return {
        run: active ? await buildRunState(db, active) : null,
        log: latest ? await buildLog(ctx, db, latest.id) : [],
        logRunId: latest ? latest.id : null,
        inventoryCount: inv.length,
        recordCount: finished.length,
        lastResult: finished[0] ? resultOf(finished[0]) : null,
      };
    },
  }),

  start_run: defineAction({
    description:
      "潜行を作成し装備フェーズへ移す。course は 10/20/30、difficulty は easy/normal/hard。返却に帰還可能階を含む。",
    request: z.object({
      course: z.number().int(),
      difficulty: z.enum(["easy", "normal", "hard"]),
    }),
    response: z.object({
      ok: z.boolean(),
      error: z.string().nullable(),
      run: runStateSchema.nullable(),
    }),
    async handler(ctx, args) {
      const db = ctx.db<typeof schema>();
      if (!(PARAMS.courses as readonly number[]).includes(args.course)) {
        return { ok: false, error: `コースは ${PARAMS.courses.join("/")} のいずれかです`, run: null };
      }
      const active = await getActiveRun(db);
      if (active) {
        return { ok: false, error: "進行中の潜行があります。先に終了（帰還／中断）してください", run: null };
      }
      const returnFloor = Math.ceil(args.course / 2);
      const inserted = await db
        .insert(schema.runs)
        .values({
          course: args.course,
          difficulty: args.difficulty,
          phase: "equip",
          floor: 0,
          returnFloor,
        })
        .returning({ id: schema.runs.id });
      const id = inserted[0]?.id;
      if (id == null) return { ok: false, error: "潜行の作成に失敗しました", run: null };
      const rows = await db.select().from(schema.runs).where(eq(schema.runs.id, id)).limit(1);
      const run = rows[0];
      if (!run) return { ok: false, error: "潜行の作成に失敗しました", run: null };
      ctx.invalidateQueries();
      return { ok: true, error: null, run: await buildRunState(db, run) };
    },
  }),

  set_equip: defineAction({
    description: "装備を確定する（最大4つ、所持品のみ）。確定すると潜行フェーズへ移る。",
    request: z.object({ item_ids: z.array(z.number().int()) }),
    response: z.object({
      ok: z.boolean(),
      error: z.string().nullable(),
      run: runStateSchema.nullable(),
    }),
    async handler(ctx, args) {
      const db = ctx.db<typeof schema>();
      const run = await getActiveRun(db);
      if (!run) return { ok: false, error: "進行中の潜行がありません", run: null };
      if (run.phase !== "equip") return { ok: false, error: "装備フェーズではありません", run: null };
      const ids = [...new Set(args.item_ids)];
      if (ids.length > PARAMS.maxEquip) {
        return { ok: false, error: `装備は最大${PARAMS.maxEquip}つまでです`, run: null };
      }
      const all = await db.select().from(schema.items);
      const owned = new Set(all.map((i) => i.id));
      if (ids.some((id) => !owned.has(id))) {
        return { ok: false, error: "所持していないアイテムが含まれています", run: null };
      }
      await db
        .update(schema.runs)
        .set({ equippedIdsJson: JSON.stringify(ids), phase: "dive" })
        .where(eq(schema.runs.id, run.id));
      const rows = await db.select().from(schema.runs).where(eq(schema.runs.id, run.id)).limit(1);
      ctx.invalidateQueries();
      return { ok: true, error: null, run: rows[0] ? await buildRunState(db, rows[0]) : null };
    },
  }),

  advance: defineAction({
    description:
      "1階進み、出来事を機械的に解決してイベントダイジェストを返す。文章待ちフェーズになるので、次に submit_text で彼女の文章を保存すること。",
    request: z.object({}),
    response: z.object({
      ok: z.boolean(),
      error: z.string().nullable(),
      digest: digestSchema.nullable(),
    }),
    async handler(ctx) {
      const db = ctx.db<typeof schema>();
      const run = await getActiveRun(db);
      if (!run) return { ...errDigest, error: "進行中の潜行がありません" };
      if (run.phase === "equip") return { ...errDigest, error: "装備が未確定です。set_equip を先に呼んでください" };
      if (run.phase === "await_text") return { ...errDigest, error: "文章待ちです。submit_text を先に呼んでください" };

      const diff = difficultyOf(run);
      const floor = run.floor + 1;
      const isBoss = floor % PARAMS.bossEvery === 0;
      let effects = parseEffects(run.effectsJson);
      let arousal = run.arousal;
      let baseSensitivity = run.sensitivity;
      let climaxes = run.climaxes;
      let clothes: string = run.clothes;
      const pending = parseGenItems(run.pendingItemsJson);
      const sensitivity = effectiveSensitivity(baseSensitivity, effects);

      const equippedIds = parseIds(run.equippedIdsJson);
      const allItems = await db.select().from(schema.items);
      const equipBonusFor = (sys: string) =>
        allItems
          .filter((i) => equippedIds.includes(i.id) && i.system === sys)
          .reduce((a, i) => a + i.bonus, 0);
      const avoidMod = effects.reduce((a, e) => a + e.avoidDelta, 0);

      let arousalPeak: number | null = null;
      const applyArousal = (delta: number): boolean => {
        arousal = round1(arousal + delta);
        let climaxed = false;
        while (arousal >= 100) {
          // 絶頂直前の最高値を記録（画面側が「絶頂の瞬間まで発情を保持し、その時に落とす」ために使う）
          arousalPeak = arousalPeak == null ? arousal : Math.max(arousalPeak, arousal);
          climaxes += 1;
          arousal = PARAMS.arousalOnClimax;
          climaxed = true;
        }
        return climaxed;
      };

      const hitTrap = (system: SystemKey, name: string, mult: number): { delta: number; climaxed: boolean } => {
        const st = trapStats(system, name);
        let delta = round1(st.arousal * mult * sensitivity);
        let climaxed = applyArousal(delta);
        if (TRAP_SYSTEMS[system].effect === "extra") {
          const extra = round1(5 * sensitivity);
          delta = round1(delta + extra);
          climaxed = applyArousal(extra) || climaxed;
        }
        if (system === "C") clothes = worsenClothes(clothes);
        const eff = effectFromTrap(system);
        if (eff) effects = [...effects, eff];
        // D系統の一部は脱力も併せ持つ（脱力ガス）— 罠名で決定的に判定
        if (system === "D" && name.includes("脱力")) {
          effects = [...effects, { label: "脱力", kind: "weakness", sensDelta: 0, avoidDelta: PARAMS.effectWeaknessAvoid, turnsLeft: PARAMS.effectWeaknessTurns }];
        }
        return { delta, climaxed };
      };

      let kind: string;
      let trapName: string | null = null;
      let system: string | null = null;
      let systemName: string | null = null;
      let labelText = "";
      let bossTypes: { key: string; label: string; note: string }[] = [];
      let avoidChance: number | null = null;
      let avoided: boolean | null = null;
      let arousalDelta = 0;
      let climaxed = false;
      let itemGained: GeneratedItem | null = null;
      let consumedItemName: string | null = null;
      let chainTrapName: string | null = null;
      let summary = "";

      if (isBoss) {
        kind = "boss";
        const base = randomTrap();
        trapName = base.name;
        system = base.system;
        systemName = TRAP_SYSTEMS[base.system].name;
        const pool = [...PARAMS.bossTypes];
        const typeCount = PARAMS.bossTypeMin + Math.floor(Math.random() * (PARAMS.bossTypeMax - PARAMS.bossTypeMin + 1));
        for (let i = 0; i < typeCount && pool.length > 0; i++) {
          const idx = Math.floor(Math.random() * pool.length);
          const t = pool.splice(idx, 1)[0];
          if (t) bossTypes.push({ key: t.key, label: t.label, note: t.note });
        }
        const typeKeys = new Set(bossTypes.map((t) => t.key));
        const st = trapStats(base.system, base.name);
        labelText = `ボストラップ：${systemName}系統「${base.name}」の概念的強化。基礎難度 ${st.difficulty}、発情増分 ${st.arousal}。強化の型：${bossTypes.map((t) => t.label).join("／")}。`;
        if (typeKeys.has("unavoidable")) {
          avoidChance = null;
          avoided = false;
        } else {
          avoidChance = Math.round(
            clamp(PARAMS.avoidBase + equipBonusFor(base.system) + avoidMod - st.difficulty - floor * PARAMS.depthPerFloor - diff.trapDiffMod, PARAMS.avoidMin, PARAMS.avoidMax),
          );
          avoided = Math.random() * 100 < avoidChance;
        }
        if (!avoided) {
          let mult = 1;
          if (typeKeys.has("sustained")) {
            mult *= PARAMS.bossSustainedMult;
            effects = [...effects, { label: "持続（ボス）：感度上昇", kind: "sustained", sensDelta: PARAMS.effectSensitivityDelta, avoidDelta: 0, turnsLeft: PARAMS.effectSustainedTurns }];
          }
          if (typeKeys.has("staged")) mult *= PARAMS.bossStagedMult;
          if (typeKeys.has("expand")) {
            mult *= PARAMS.bossExpandMult;
            clothes = worsenClothes(clothes);
          }
          if (typeKeys.has("price")) {
            const consumed = pending.pop();
            if (consumed) {
              consumedItemName = consumed.name;
              mult *= 0.5;
            }
          }
          const hit = hitTrap(base.system, base.name, mult);
          arousalDelta = round1(arousalDelta + hit.delta);
          climaxed = hit.climaxed || climaxed;
          if (typeKeys.has("chain")) {
            const chain = randomTrap();
            chainTrapName = chain.name;
            const cst = trapStats(chain.system, chain.name);
            const cDelta = round1(cst.arousal * sensitivity);
            arousalDelta = round1(arousalDelta + cDelta);
            climaxed = applyArousal(cDelta) || climaxed;
            if (chain.system === "C") clothes = worsenClothes(clothes);
            const ceff = effectFromTrap(chain.system);
            if (ceff) effects = [...effects, ceff];
          }
        }
        if (climaxes < PARAMS.climaxLimit) {
          itemGained = generateItem(diff.lootValueMult, true);
          pending.push(itemGained);
        }
        summary = avoided
          ? `${floor}階層ボス「${trapName}」を回避した。ボス産アイテムを仮保持した。`
          : `${floor}階層ボス「${trapName}」が発動した（型：${bossTypes.map((t) => t.label).join("／")}）。`;
      } else {
        const w = PARAMS.eventWeights;
        const total = w.quiet + w.chest + w.trap;
        const roll = Math.random() * total;
        if (roll < w.quiet) {
          kind = "quiet";
          labelText = "静かな回廊。何も起こらなかった。";
          const before = arousal;
          arousal = Math.max(0, round1(arousal - PARAMS.quietArousalDecay));
          arousalDelta = round1(arousal - before);
          if (baseSensitivity > 1.0) baseSensitivity = round1(Math.max(1.0, baseSensitivity - PARAMS.sensitivityDecay));
          else if (baseSensitivity < 1.0) baseSensitivity = round1(Math.min(1.0, baseSensitivity + PARAMS.sensitivityDecay));
          summary = `${floor}階：静かな回廊。発情が少し冷めた。`;
        } else if (roll < w.quiet + w.chest) {
          kind = "chest";
          labelText = "宝箱。開けるとアイテムが1つ入っていた。";
          itemGained = generateItem(diff.lootValueMult, false);
          pending.push(itemGained);
          summary = `${floor}階：宝箱から「${itemGained.name}」を仮保持した。`;
        } else {
          kind = "trap";
          const t = randomTrap();
          trapName = t.name;
          system = t.system;
          systemName = TRAP_SYSTEMS[t.system].name;
          const st = trapStats(t.system, t.name);
          const tmpl = TRAP_SYSTEMS[t.system];
          labelText = `${systemName}系統の罠。基礎難度 ${st.difficulty}、発情増分 ${st.arousal}${tmpl.effectNote ? `、主な効果：${tmpl.effectNote}` : ""}。`;
          avoidChance = Math.round(
            clamp(PARAMS.avoidBase + equipBonusFor(t.system) + avoidMod - st.difficulty - floor * PARAMS.depthPerFloor - diff.trapDiffMod, PARAMS.avoidMin, PARAMS.avoidMax),
          );
          avoided = Math.random() * 100 < avoidChance;
          if (!avoided) {
            const hit = hitTrap(t.system, t.name, 1);
            arousalDelta = hit.delta;
            climaxed = hit.climaxed;
          }
          summary = avoided
            ? `${floor}階：${systemName}系統「${trapName}」を回避した（成功率${avoidChance}%）。`
            : `${floor}階：${systemName}系統「${trapName}」が発動した。`;
        }
      }

      // 持続効果のターンを進める
      effects = effects
        .map((e) => ({ ...e, turnsLeft: e.turnsLeft - 1 }))
        .filter((e) => e.turnsLeft > 0);
      const sensitivityAfter = effectiveSensitivity(baseSensitivity, effects);

      let finishKind: string | null = null;
      if (climaxes >= PARAMS.climaxLimit) finishKind = "defeat";
      else if (isBoss && floor >= run.course) finishKind = "clear";

      const digest: Digest = {
        floor,
        kind,
        isBoss,
        trapName,
        system,
        systemName,
        labelText,
        bossTypes,
        avoidChance,
        avoided,
        arousalDelta,
        arousal,
        arousalPeak,
        sensitivity: sensitivityAfter,
        climaxed,
        climaxCount: climaxes,
        climaxDelta: climaxes - run.climaxes,
        effects,
        clothes,
        canReturn: floor >= run.returnFloor,
        returnFloor: run.returnFloor,
        pendingCount: pending.length,
        itemGained: itemGained ? withSysName(itemGained) : null,
        consumedItemName,
        chainTrapName,
        runFinished: finishKind != null,
        finishKind,
        summary,
      };

      await db
        .update(schema.runs)
        .set({
          floor,
          arousal,
          sensitivity: baseSensitivity,
          climaxes,
          clothes: clothes as RunRow["clothes"],
          effectsJson: JSON.stringify(effects),
          pendingItemsJson: JSON.stringify(pending),
          phase: "await_text",
          pendingFinish: finishKind === "clear" ? "clear" : finishKind === "defeat" ? "defeat" : null,
        })
        .where(eq(schema.runs.id, run.id));
      await db.insert(schema.events).values({
        runId: run.id,
        floor,
        digestJson: JSON.stringify(digest),
      });
      ctx.invalidateQueries();
      return { ok: true, error: null, digest };
    },
  }),

  submit_text: defineAction({
    description:
      "文章待ち中のイベントに彼女の文章（セリフ＋地の文＋擬音）を保存し、潜行フェーズへ戻す。そのイベントで潜行が終了していた場合は結果を確定して返す。" +
      "文章にはタイミングマーカー行を置ける：単独行で [[trap]]（罠に引っかかった瞬間：画面が絵のシーンへ遷移する）、[[scene-end]]（罠の場面を抜けた所：3D回廊へ戻る。省略時は文章の終わりで戻る）、[[climax]]（絶頂した瞬間：画面が明滅し、絶頂カウントと発情の低下がその時点で表示に反映される）。" +
      "マーカー行はパースされて表示テキストには出ない。置く場所は「その出来事が文章中で示される行」の直前。罠を回避した階では [[trap]] を置かない。" +
      "真実（当たったか・絶頂したか・回数）は advance のダイジェストが決めるので、マーカーはタイミング専用：ダイジェストと矛盾するマーカーは画面側で無視され、不足分は文章の終わりで適用される。" +
      "罠に当たった階では、提出と同時にその場面の画像生成リクエスト（NAI形式のタグプロンプト＋ネガティブプロンプト）がサーバー側で自動作成され、イベント記録にスナップショット保存される（画像生成APIの有無にかかわらない）。絵の実生成は generate_event_image が保存済みプロンプトで行う。",
    request: z.object({ text: z.string() }),
    response: z.object({
      ok: z.boolean(),
      error: z.string().nullable(),
      finished: z.boolean(),
      result: resultSchema.nullable(),
    }),
    async handler(ctx, args) {
      const db = ctx.db<typeof schema>();
      const run = await getActiveRun(db);
      if (!run) return { ok: false, error: "進行中の潜行がありません", finished: false, result: null };
      if (run.phase !== "await_text") return { ok: false, error: "文章待ちではありません", finished: false, result: null };
      const evs = await db
        .select()
        .from(schema.events)
        .where(eq(schema.events.runId, run.id))
        .orderBy(desc(schema.events.id))
        .limit(1);
      const ev = evs[0];
      if (!ev) return { ok: false, error: "待ち中のイベントが見つかりません", finished: false, result: null };
      // マーカー行をパースして表示テキストから除去し、タイミング情報として別に保存する
      const parsed = parseTextMarks(args.text);
      await db
        .update(schema.events)
        .set({ text: parsed.text, marksJson: JSON.stringify(parsed.marks) })
        .where(eq(schema.events.id, ev.id));
      // 罠シーンの生成リクエスト：API の有無にかかわらず、文章提出のこの時点で
      // プロンプト（NAI形式タグ＋ネガティブ）を組み立ててスナップショット保存する。
      // 後から generate_event_image がこの保存済みプロンプトをそのまま使う。
      // 既にプロンプトがあるイベントは書き換えない（同一性の保持）。
      if (!ev.imagePromptJson) {
        const digest = JSON.parse(ev.digestJson) as Digest;
        const prompt = promptForEvent(digest, parsed.text, parsed.marks);
        if (prompt) {
          await db
            .update(schema.events)
            .set({ imagePromptJson: JSON.stringify(prompt), imageStatus: "awaiting" })
            .where(eq(schema.events.id, ev.id));
        }
      }
      if (run.pendingFinish) {
        const result = await finalizeRun(db, run, run.pendingFinish === "clear" ? "クリア" : "敗北");
        ctx.invalidateQueries();
        return { ok: true, error: null, finished: true, result };
      }
      await db.update(schema.runs).set({ phase: "dive" }).where(eq(schema.runs.id, run.id));
      ctx.invalidateQueries();
      return { ok: true, error: null, finished: false, result: null };
    },
  }),

  prepare_image_prompts: defineAction({
    description:
      "文章が保存済みで生成リクエスト未作成の全イベント（過去の記録を含む）について、罠シーンの画像生成リクエスト（NAI形式プロンプト）を後付けで作成しスナップショット保存する。既にプロンプトがあるイベントは書き換えない。作成件数と対象外件数を返す。",
    request: z.object({}),
    response: z.object({
      prepared: z.number(),
      skipped: z.number(),
    }),
    async handler(ctx) {
      const db = ctx.db<typeof schema>();
      const rows = await db.select().from(schema.events).orderBy(asc(schema.events.id));
      let prepared = 0;
      let skipped = 0;
      for (const ev of rows) {
        if (ev.imagePromptJson || !ev.text) {
          skipped++;
          continue;
        }
        const digest = JSON.parse(ev.digestJson) as Digest;
        const prompt = promptForEvent(digest, ev.text, parseMarks(ev.marksJson));
        if (!prompt) {
          skipped++;
          continue;
        }
        await db
          .update(schema.events)
          .set({ imagePromptJson: JSON.stringify(prompt), imageStatus: "awaiting" })
          .where(eq(schema.events.id, ev.id));
        prepared++;
      }
      if (prepared > 0) ctx.invalidateQueries();
      return { prepared, skipped };
    },
  }),

  generate_event_image: defineAction({
    description:
      "指定した潜行・階の罠シーン画像を、記録に保存済みの生成リクエスト（NAI形式プロンプトのスナップショット）から生成し、そのイベント記録の画像スロットへ保存する（後から生成する入口）。プロンプト未作成なら先に prepare_image_prompts か submit_text が必要。生成済みの場合は既存の絵をそのまま返す（冪等）。",
    request: z.object({
      run_id: z.number().int(),
      floor: z.number().int(),
    }),
    response: z.object({
      ok: z.boolean(),
      error: z.string().nullable(),
      status: z.string().nullable(), // none | awaiting | generating | ready | failed
      imageUrl: z.string().nullable(),
    }),
    async handler(ctx, args) {
      const db = ctx.db<typeof schema>();
      const rows = await db
        .select()
        .from(schema.events)
        .where(eq(schema.events.runId, args.run_id))
        .orderBy(desc(schema.events.id));
      const ev = rows.find((r) => r.floor === args.floor);
      if (!ev) return { ok: false, error: "指定された階のイベントが見つかりません", status: null, imageUrl: null };
      if (ev.imageStatus === "ready" && ev.imageBlobKey) {
        return { ok: true, error: null, status: "ready", imageUrl: await ctx.blobs.getUrl(ev.imageBlobKey) };
      }
      const prompt = parseImagePrompt(ev.imagePromptJson);
      if (!prompt) {
        return {
          ok: false,
          error: "この場面には生成リクエスト（プロンプト）が未作成です。prepare_image_prompts で作成してください",
          status: ev.imageStatus,
          imageUrl: null,
        };
      }
      await db
        .update(schema.events)
        .set({ imageStatus: "generating" })
        .where(eq(schema.events.id, ev.id));
      try {
        // 生成アダプタ：保存済みプロンプト（NAIタグ形式）をそのまま使う。
        // 媒体側の都合でタグが効かない場合も、根拠抜粋で場面を伝える。
        const instruction = [
          "Create one anime-style illustration for a scene in a dark-fantasy dungeon trap game, following this NovelAI-style tag prompt exactly:",
          prompt.positive,
          "",
          `Negative prompt (avoid all of these): ${prompt.negative}`,
          "",
          `Scene basis (the heroine's own account of this moment, for composition reference): ${prompt.sceneExcerpt}`,
          "One image only, no text, no watermark.",
        ].join("\n");
        const media = await ctx.tool.generate_media(instruction, {
          orientation: "landscape",
          key: `etd-scene/run-${args.run_id}-floor-${args.floor}`,
        });
        await db
          .update(schema.events)
          .set({ imageBlobKey: media.blobKey, imageStatus: "ready" })
          .where(eq(schema.events.id, ev.id));
        ctx.invalidateQueries();
        return { ok: true, error: null, status: "ready", imageUrl: media.url };
      } catch {
        await db
          .update(schema.events)
          .set({ imageStatus: "failed" })
          .where(eq(schema.events.id, ev.id));
        ctx.invalidateQueries();
        return { ok: false, error: "画像生成に失敗しました。記録とプロンプトは残っているので、再試行できます", status: "failed", imageUrl: null };
      }
    },
  }),

  return_home: defineAction({
    description:
      "帰還可能階に到達していれば生還で終了し、仮保持アイテムを所持品へ確定する。結果ダイジェストを返す。",
    request: z.object({}),
    response: z.object({
      ok: z.boolean(),
      error: z.string().nullable(),
      result: resultSchema.nullable(),
    }),
    async handler(ctx) {
      const db = ctx.db<typeof schema>();
      const run = await getActiveRun(db);
      if (!run) return { ok: false, error: "進行中の潜行がありません", result: null };
      if (run.phase === "await_text") return { ok: false, error: "文章待ちです。submit_text を先に呼んでください", result: null };
      if (run.phase === "equip") return { ok: false, error: "まだ潜行を開始していません（中断は abandon_run）", result: null };
      if (run.floor < run.returnFloor) {
        return { ok: false, error: `帰還は${run.returnFloor}階到達後から可能です（現在${run.floor}階）`, result: null };
      }
      const result = await finalizeRun(db, run, "生還");
      ctx.invalidateQueries();
      return { ok: true, error: null, result };
    },
  }),

  abandon_run: defineAction({
    description: "潜行を中断する。仮保持アイテムは失うが敗北扱いにはならない。装備など既存の所持品は消えない。",
    request: z.object({}),
    response: z.object({
      ok: z.boolean(),
      error: z.string().nullable(),
      result: resultSchema.nullable(),
    }),
    async handler(ctx) {
      const db = ctx.db<typeof schema>();
      const run = await getActiveRun(db);
      if (!run) return { ok: false, error: "進行中の潜行がありません", result: null };
      const result = await finalizeRun(db, run, "中断");
      ctx.invalidateQueries();
      return { ok: true, error: null, result };
    },
  }),

  list_inventory: defineAction({
    description: "所持品の一覧を返す（永続。敗北でも消えない）。",
    request: z.object({}),
    response: z.object({
      items: z.array(
        invItemSchema.extend({ systemName: z.string() }),
      ),
    }),
    async handler(ctx) {
      const db = ctx.db<typeof schema>();
      const rows = await db.select().from(schema.items).orderBy(desc(schema.items.id));
      return {
        items: rows.map((i) => ({
          id: i.id,
          name: i.name,
          system: i.system,
          systemName: TRAP_SYSTEMS[i.system as SystemKey]?.name ?? i.system,
          bonus: i.bonus,
          rarity: i.rarity,
        })),
      };
    },
  }),

  list_records: defineAction({
    description: "過去の潜行記録の一覧を返す（結果・到達階・絶頂回数・確定／喪失アイテム・各階のログ）。",
    request: z.object({ limit: z.number().int().positive().max(50).default(20) }),
    response: z.object({
      records: z.array(
        resultSchema.extend({
          startedAt: z.string(),
          finishedAt: z.string().nullable(),
          log: z.array(logEntrySchema),
        }),
      ),
    }),
    async handler(ctx, args) {
      const db = ctx.db<typeof schema>();
      const rows = await db
        .select()
        .from(schema.runs)
        .where(eq(schema.runs.phase, "finished"))
        .orderBy(desc(schema.runs.id))
        .limit(args.limit);
      const records = [];
      for (const r of rows) {
        records.push({
          ...resultOf(r),
          startedAt: r.startedAt.toISOString(),
          finishedAt: r.finishedAt ? r.finishedAt.toISOString() : null,
          log: await buildLog(ctx, db, r.id),
        });
      }
      return { records };
    },
  }),

  get_params: defineAction({
    description: "試作用パラメータ（全て暫定）と罠の出現表を返す。画面のパラメータタブ用。",
    request: z.object({}),
    response: z.object({
      provisional: z.boolean(),
      courses: z.array(z.number()),
      dailyChallengeLimit: z.number().nullable(),
      bossEvery: z.number(),
      returnRule: z.string(),
      climaxLimit: z.number(),
      maxEquip: z.number(),
      arousalOnClimax: z.number(),
      quietArousalDecay: z.number(),
      sensitivityDecay: z.number(),
      sensitivityMin: z.number(),
      sensitivityMax: z.number(),
      avoidBase: z.number(),
      avoidMin: z.number(),
      avoidMax: z.number(),
      depthPerFloor: z.number(),
      eventWeights: z.object({ quiet: z.number(), chest: z.number(), trap: z.number() }),
      difficulties: z.array(
        z.object({ key: z.string(), label: z.string(), trapDiffMod: z.number(), lootValueMult: z.number() }),
      ),
      itemBonusPerRarity: z.number(),
      itemBonusRandMax: z.number(),
      rarityWeights: z.array(z.number()),
      bossRarityWeights: z.array(z.number()),
      effectSensitivityDelta: z.number(),
      effectSensitivityTurns: z.number(),
      effectWeaknessAvoid: z.number(),
      effectWeaknessTurns: z.number(),
      effectBindAvoid: z.number(),
      effectBindTurns: z.number(),
      effectSustainedTurns: z.number(),
      bossTypes: z.array(bossTypeSchema),
      bossSustainedMult: z.number(),
      bossStagedMult: z.number(),
      bossExpandMult: z.number(),
      systems: z.array(
        z.object({
          key: z.string(),
          name: z.string(),
          diffMin: z.number(),
          diffMax: z.number(),
          arousalMin: z.number(),
          arousalMax: z.number(),
          effectNote: z.string().nullable(),
          traps: z.array(z.string()),
        }),
      ),
    }),
    async handler() {
      return {
        provisional: PARAMS.provisional,
        courses: [...PARAMS.courses],
        dailyChallengeLimit: PARAMS.dailyChallengeLimit,
        bossEvery: PARAMS.bossEvery,
        returnRule: PARAMS.returnRule,
        climaxLimit: PARAMS.climaxLimit,
        maxEquip: PARAMS.maxEquip,
        arousalOnClimax: PARAMS.arousalOnClimax,
        quietArousalDecay: PARAMS.quietArousalDecay,
        sensitivityDecay: PARAMS.sensitivityDecay,
        sensitivityMin: PARAMS.sensitivityMin,
        sensitivityMax: PARAMS.sensitivityMax,
        avoidBase: PARAMS.avoidBase,
        avoidMin: PARAMS.avoidMin,
        avoidMax: PARAMS.avoidMax,
        depthPerFloor: PARAMS.depthPerFloor,
        eventWeights: { ...PARAMS.eventWeights },
        difficulties: Object.entries(PARAMS.difficulties).map(([key, d]) => ({
          key,
          label: d.label,
          trapDiffMod: d.trapDiffMod,
          lootValueMult: d.lootValueMult,
        })),
        itemBonusPerRarity: PARAMS.itemBonusPerRarity,
        itemBonusRandMax: PARAMS.itemBonusRandMax,
        rarityWeights: [...PARAMS.rarityWeights],
        bossRarityWeights: [...PARAMS.bossRarityWeights],
        effectSensitivityDelta: PARAMS.effectSensitivityDelta,
        effectSensitivityTurns: PARAMS.effectSensitivityTurns,
        effectWeaknessAvoid: PARAMS.effectWeaknessAvoid,
        effectWeaknessTurns: PARAMS.effectWeaknessTurns,
        effectBindAvoid: PARAMS.effectBindAvoid,
        effectBindTurns: PARAMS.effectBindTurns,
        effectSustainedTurns: PARAMS.effectSustainedTurns,
        bossTypes: PARAMS.bossTypes.map((t) => ({ key: t.key, label: t.label, note: t.note })),
        bossSustainedMult: PARAMS.bossSustainedMult,
        bossStagedMult: PARAMS.bossStagedMult,
        bossExpandMult: PARAMS.bossExpandMult,
        systems: SYSTEM_KEYS.map((k) => {
          const t = TRAP_SYSTEMS[k];
          return {
            key: k,
            name: t.name,
            diffMin: t.diffMin,
            diffMax: t.diffMax,
            arousalMin: t.arousalMin,
            arousalMax: t.arousalMax,
            effectNote: t.effectNote ?? null,
            traps: [...t.traps],
          };
        }),
      };
    },
  }),
} satisfies ActionsModule;
