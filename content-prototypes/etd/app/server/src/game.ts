// ETD試作 ゲームロジックとパラメータ。
// 数値はすべて暫定。バランス調整はこのファイルの PARAMS だけを触る。
// 真実（抽選・判定・体の状態）はすべてサーバー側のここで決まる。

export const PARAMS = {
  provisional: true, // 全数値が暫定であることの明示
  courses: [10, 20, 30],
  dailyChallengeLimit: null as number | null, // テスト用暫定：無制限
  bossEvery: 10, // 10階ごとにボストラップ
  returnRule: "潜行長さの半分（切り上げ）の階に到達するまで帰還不可",
  climaxLimit: 3, // 絶頂3回で敗北
  maxEquip: 4,
  // 体の真実
  arousalOnClimax: 30, // 絶頂したら発情は30に戻る
  quietArousalDecay: 8, // 静かなターン：発情 −8
  sensitivityDecay: 0.03, // 静かなターン：感度は1.0へ0.03ずつ減衰
  sensitivityMin: 1.0,
  sensitivityMax: 3.0,
  // 回避判定： 成功率% = clamp(avoidBase + 装備ボーナス − 罠の基礎難度 − 階×depthPerFloor − 難易度係数, avoidMin, avoidMax)
  avoidBase: 55,
  avoidMin: 5,
  avoidMax: 95,
  depthPerFloor: 1,
  // 出来事抽選の重み（通常階）
  eventWeights: { quiet: 25, chest: 12, trap: 63 },
  // 難易度
  difficulties: {
    easy: { label: "やさしい", trapDiffMod: -10, lootValueMult: 0.8 },
    normal: { label: "ふつう", trapDiffMod: 0, lootValueMult: 1.0 },
    hard: { label: "つらい", trapDiffMod: 10, lootValueMult: 1.6 },
  } as Record<string, { label: string; trapDiffMod: number; lootValueMult: number }>,
  // アイテム：回避ボーナス = レアリティ×itemBonusPerRarity + 0〜itemBonusRandMax
  itemBonusPerRarity: 6,
  itemBonusRandMax: 5,
  // レアリティ抽選の基礎重み（1〜5）。価値係数は高レア側の重みに効く
  rarityWeights: [30, 28, 22, 13, 7],
  bossRarityWeights: [8, 20, 30, 25, 17], // ボス産は高レアが出やすい
  // 持続効果（試作版の機械的帰結）
  effectSensitivityDelta: 0.2, // 感度系の罠：感度+0.2
  effectSensitivityTurns: 3,
  effectWeaknessAvoid: -10, // 脱力：回避−10
  effectWeaknessTurns: 2,
  effectBindAvoid: -15, // 拘束：次の回避−15
  effectBindTurns: 1,
  effectSustainedTurns: 3, // N系統・ボス「持続」
  // ボストラップ強化の型（試作版の機械的帰結）
  bossTypes: [
    { key: "unavoidable", label: "不可避化", note: "回避判定なし" },
    { key: "sustained", label: "持続", note: "発情増分×1.5、感度+0.2が3ターン" },
    { key: "chain", label: "連鎖", note: "追加で別の罠の効果を判定なしで適用" },
    { key: "staged", label: "段階", note: "発情増分×1.6" },
    { key: "expand", label: "対象の拡大", note: "服の状態が悪化し増分×1.2" },
    { key: "price", label: "代償の提示", note: "仮保持アイテムがあれば1つ消費して増分半減、無ければ全量" },
  ],
  bossTypeMin: 1,
  bossTypeMax: 2,
  bossSustainedMult: 1.5,
  bossStagedMult: 1.6,
  bossExpandMult: 1.2,
} as const;

export type SystemKey =
  | "A" | "B" | "C" | "D" | "E" | "F" | "G" | "H" | "I" | "J"
  | "K" | "L" | "M" | "N" | "O" | "P" | "Q" | "R" | "S";

export interface TrapTemplate {
  name: string;
  diffMin: number;
  diffMax: number;
  arousalMin: number;
  arousalMax: number;
  effect?: "bind" | "clothes" | "sensitivity" | "extra" | "sustained" | "weakness";
  effectNote?: string;
  traps: string[];
}

// 系統テンプレートと罠名（出現表／レッテル表）
export const TRAP_SYSTEMS: Record<SystemKey, TrapTemplate> = {
  A: { name: "地形", diffMin: 5, diffMax: 15, arousalMin: 8, arousalMax: 18, traps: ["落とし穴（粘液床）", "滑る床", "粘着床", "沈む床", "落ちる網・蔦", "壁の迫る部屋", "回転・移動床", "温泉の部屋"] },
  B: { name: "拘束", diffMin: 10, diffMax: 20, arousalMin: 12, arousalMax: 24, effect: "bind", effectNote: "拘束（次の回避−15）", traps: ["罠の縄・鎖", "粘液の繭", "蜘蛛の巣", "蔦の絡みつき", "部分石化", "磁力・重力の罠", "拘束つきの椅子・台", "自動装着の首輪・手枷"] },
  C: { name: "衣服装備", diffMin: 5, diffMax: 15, arousalMin: 6, arousalMax: 14, effect: "clothes", effectNote: "服の状態が悪化", traps: ["防具を溶かす酸", "布を食べる虫", "引き裂く棘・フック", "装備を盗む小動物", "呪いの装備", "水濡れ", "突風の罠", "所持品の重量加算", "武器の逆襲", "予備の服の書き換え"] },
  D: { name: "薬物", diffMin: 10, diffMax: 20, arousalMin: 16, arousalMax: 30, effect: "sensitivity", effectNote: "感度上昇", traps: ["媚薬ガス", "媚薬の花粉・霧", "媚薬の泉", "感度だけ上げる毒", "脱力ガス", "発情毒の棘", "酩酊ガス", "持続性媚薬"] },
  E: { name: "生物", diffMin: 10, diffMax: 22, arousalMin: 14, arousalMax: 28, traps: ["スライム", "触手生物", "食虫植物", "吸盤の生物", "ミミック", "魅了の目", "妖精のいたずら", "寄生を脅す虫", "吸血と電流の虫", "体内で増える胞子キノコ", "共生する衣服の種", "体力を奪う栄養の床"] },
  F: { name: "機械", diffMin: 8, diffMax: 18, arousalMin: 12, arousalMax: 24, traps: ["マッサージ器具", "拘束台と機械アーム", "洗浄装置", "振動する乗り物", "弱い電撃", "自動着せ替え装置", "録画・中継装置"] },
  G: { name: "精神", diffMin: 12, diffMax: 22, arousalMin: 10, arousalMax: 20, effect: "sensitivity", effectNote: "感度上昇", traps: ["魅了の魔法陣", "幻覚", "催眠の音・香", "夢落とし", "命令の紋章", "感情増幅の結界", "鏡の間", "鏡像の双子", "目的を忘れる香炉", "自責を快感に変えるミスト", "執着の首飾り", "焦燥の呪い"] },
  H: { name: "身体変化", diffMin: 10, diffMax: 20, arousalMin: 12, arousalMax: 22, effect: "sensitivity", effectNote: "感度上昇", traps: ["感度の呪い", "耳・尻尾", "敏感になる魔法", "小さくなる呪い", "勝手に声が出る呪い", "発情強制の魔法"] },
  I: { name: "羞恥公開", diffMin: 5, diffMax: 12, arousalMin: 8, arousalMax: 16, traps: ["透けて見える結界", "拡声の罠", "実況中継の魔法", "スポットライトの舞台", "記録の魔法陣", "羞恥度の刻印", "共感リンク", "視線の圧", "記録の再生"] },
  J: { name: "快感蓄積", diffMin: 12, diffMax: 24, arousalMin: 20, arousalMax: 34, traps: ["寸止めの結界", "快感が溜まる紋章", "絶頂で開く扉", "振動し続ける装備品", "くすぐり", "浸透圧の塗料"] },
  K: { name: "感覚環境", diffMin: 5, diffMax: 15, arousalMin: 8, arousalMax: 18, traps: ["暗闇と目隠し", "聴覚の遮断・増幅", "体温の上がる部屋", "冷気の部屋", "重力の変化", "局所発汗の陣", "代謝の暴走", "傾斜するフロア", "足裏の圧着マット"] },
  L: { name: "報酬偽装", diffMin: 8, diffMax: 16, arousalMin: 14, arousalMax: 24, traps: ["回復の泉", "呪いの宝物", "代償の祭壇", "ベッドの罠", "媚薬の果実"] },
  M: { name: "連鎖", diffMin: 12, diffMax: 22, arousalMin: 14, arousalMax: 26, effect: "extra", effectNote: "小さい追加増分", traps: ["暴れるほど締まる拘束", "声で反応する罠", "逃げるほど追う罠", "解くほど増える罠", "時間で強くなる罠", "プレッシャー・カスケード", "装備転移の泡"] },
  N: { name: "持続", diffMin: 8, diffMax: 18, arousalMin: 10, arousalMax: 20, effect: "sustained", effectNote: "持続系の効果", traps: ["続く感度上昇", "残る紋章", "ついた匂い", "服の損傷の継続"] },
  O: { name: "汚染定着", diffMin: 8, diffMax: 18, arousalMin: 10, arousalMax: 20, effect: "sensitivity", effectNote: "感度上昇", traps: ["液体金属のコーティング", "発汗・体臭の胞子", "消えない蛍光の紋様", "皮脂の噴霧"] },
  P: { name: "音響共鳴", diffMin: 8, diffMax: 18, arousalMin: 10, arousalMax: 20, traps: ["周波数同調の回廊", "残響の回廊", "共鳴の鐘"] },
  Q: { name: "接触反発", diffMin: 8, diffMax: 18, arousalMin: 8, arousalMax: 16, traps: ["斥力の壁", "衣装の反逆", "摩擦消失の回廊", "弾力バリアの狭路"] },
  R: { name: "代償交換", diffMin: 10, diffMax: 20, arousalMin: 12, arousalMax: 22, traps: ["快感の変換の茨", "選択の天秤", "罠を引き寄せる加速薬", "精神力を食う扉"] },
  S: { name: "属性反射", diffMin: 10, diffMax: 20, arousalMin: 12, arousalMax: 24, effect: "sensitivity", effectNote: "感度上昇", traps: ["逆転の結界", "属性付与の泉", "共鳴の刻印", "フェロモンの放出"] },
};

export const SYSTEM_KEYS = Object.keys(TRAP_SYSTEMS) as SystemKey[];

// ---- 乱数と決定的ばらつき ----

/** 名前から決定的に 0..1 を返す（同じ罠はいつも同じ基礎値寄りになる） */
export function hash01(text: string, salt: number): number {
  let h = 2166136261 ^ salt;
  for (let i = 0; i < text.length; i++) {
    h ^= text.charCodeAt(i);
    h = Math.imul(h, 16777619);
  }
  return ((h >>> 0) % 10000) / 10000;
}

function randInt(min: number, max: number): number {
  return min + Math.floor(Math.random() * (max - min + 1));
}

function pick<T>(arr: readonly T[]): T {
  const v = arr[Math.floor(Math.random() * arr.length)];
  if (v === undefined) throw new Error("pick from empty array");
  return v;
}

/** 罠1件の基礎値。名前から決定的にレンジ内でばらつかせる */
export function trapStats(system: SystemKey, trapName: string): { difficulty: number; arousal: number } {
  const t = TRAP_SYSTEMS[system];
  const difficulty = Math.round(t.diffMin + hash01(trapName, 11) * (t.diffMax - t.diffMin));
  const arousal = Math.round((t.arousalMin + hash01(trapName, 77) * (t.arousalMax - t.arousalMin)) * 10) / 10;
  return { difficulty, arousal };
}

export function randomTrap(): { system: SystemKey; name: string } {
  const system = pick(SYSTEM_KEYS);
  return { system, name: pick(TRAP_SYSTEMS[system].traps) };
}

// ---- アイテム生成 ----

const ITEM_PARTS: Record<string, string[]> = {
  default: ["腕輪", "指輪", "首飾り", "耳飾り", "腰帯", "護符", "肩当て", "靴"],
};
const ITEM_MATS = ["銀糸", "月光石", "黒曜石", "蒼玉", "緋緋石", "琥珀", "真珠母貝", "鉄心木", "星屑ガラス", "竜骨"];

export interface GeneratedItem {
  name: string;
  system: SystemKey;
  bonus: number;
  rarity: number;
}

function rollRarity(weights: readonly number[], valueMult: number): number {
  // 価値係数は高レアリティ側の重みを押し上げる
  const adjusted = weights.map((w, i) => w * Math.pow(valueMult, i * 0.6));
  const total = adjusted.reduce((a, b) => a + b, 0);
  let r = Math.random() * total;
  for (let i = 0; i < adjusted.length; i++) {
    r -= adjusted[i] ?? 0;
    if (r <= 0) return i + 1;
  }
  return adjusted.length;
}

export function generateItem(valueMult: number, boss: boolean): GeneratedItem {
  const system = pick(SYSTEM_KEYS);
  const rarity = rollRarity(boss ? PARAMS.bossRarityWeights : PARAMS.rarityWeights, valueMult);
  const bonus = rarity * PARAMS.itemBonusPerRarity + randInt(0, PARAMS.itemBonusRandMax);
  const sysName = TRAP_SYSTEMS[system].name;
  const part = pick(ITEM_PARTS.default ?? ["腕輪"]);
  const mat = pick(ITEM_MATS);
  return { name: `対${sysName}の${mat}の${part}`, system, bonus, rarity };
}

// ---- 持続効果 ----

export interface ActiveEffect {
  label: string;
  kind: "sensitivity" | "weakness" | "bind" | "sustained";
  sensDelta: number; // 感度への加算（kind=sensitivity/sustained）
  avoidDelta: number; // 回避率への加算
  turnsLeft: number;
}

/** 罠の系統効果を、当たったときに付く持続効果へ変換する */
export function effectFromTrap(system: SystemKey): ActiveEffect | null {
  const t = TRAP_SYSTEMS[system];
  switch (t.effect) {
    case "sensitivity":
      return { label: `感度上昇（${t.name}）`, kind: "sensitivity", sensDelta: PARAMS.effectSensitivityDelta, avoidDelta: 0, turnsLeft: PARAMS.effectSensitivityTurns };
    case "bind":
      return { label: "拘束", kind: "bind", sensDelta: 0, avoidDelta: PARAMS.effectBindAvoid, turnsLeft: PARAMS.effectBindTurns };
    case "sustained":
      return { label: `持続する感度上昇（${t.name}）`, kind: "sustained", sensDelta: PARAMS.effectSensitivityDelta, avoidDelta: 0, turnsLeft: PARAMS.effectSustainedTurns };
    default:
      return null;
  }
}

/** 脱力ガス専用（D系統の一部として簡易に扱う：脱力効果） */
export function weaknessEffect(): ActiveEffect {
  return { label: "脱力", kind: "weakness", sensDelta: 0, avoidDelta: PARAMS.effectWeaknessAvoid, turnsLeft: PARAMS.effectWeaknessTurns };
}

export function clamp(v: number, min: number, max: number): number {
  return Math.min(max, Math.max(min, v));
}

export function round1(v: number): number {
  return Math.round(v * 10) / 10;
}

// ---- 文章内タイミングマーカー ----
// 彼女の文章には、その出来事が「文中のどこで起きたか」を示すマーカー行を置ける。
// マーカーは表示テキストから取り除かれ、タイミング情報としてだけ保存される。
// 真実（罠に当たったか／絶頂したか／回数）はダイジェスト側が決め、
// マーカーは演出のタイミング専用（余分なマーカーはクライアント側で無視される）。

export interface TextMark {
  /** このマーカーが位置する表示行の直前の行数（= その行を表示した時点で発火） */
  atLine: number;
  type: "trap" | "climax" | "scene-end";
}

const MARK_LINES: Record<string, TextMark["type"]> = {
  "[[trap]]": "trap",
  "[[climax]]": "climax",
  "[[scene-end]]": "scene-end",
};

/**
 * 文章からマーカー行を取り除き、表示行（空白でない行を trim したもの）の列と
 * マーカー列に分ける。クライアントの行分割（trim＋空行除去）と一致させる。
 */
export function parseTextMarks(raw: string): { text: string; marks: TextMark[] } {
  const displayLines: string[] = [];
  const marks: TextMark[] = [];
  for (const rawLine of raw.split(/\r?\n/)) {
    const line = rawLine.trim();
    const markType = MARK_LINES[line];
    if (markType) {
      marks.push({ atLine: displayLines.length, type: markType });
    } else if (line.length > 0) {
      displayLines.push(line);
    }
  }
  return { text: displayLines.join("\n"), marks };
}

// ---- 罠シーンの生成リクエスト（プロンプト） ----
// API の有無にかかわらず、文章が提出された時点でサーバー側が機械的に組み立て、
// イベント記録へスナップショット保存する。後から生成する時は保存済みプロンプトを
// そのまま使う（テンプレートが将来変わっても過去の場面の指示は書き換えない）。
// 組み方は NAI（NovelAI）前提：自然文ではなくカンマ区切りのタグ形式
// （Danbooru 系タグ＋品質タグ）を基本とし、ネガティブプロンプトも対で持つ。
// 根拠はダイジェスト（罠名・系統・服の状態・絶頂の有無）と、
// [[trap]]〜[[scene-end]] の場面テキスト抜粋だけ。文章の創作はしない。

export interface SceneImagePromptInput {
  trapName: string;
  system: SystemKey;
  systemName: string;
  isBoss: boolean;
  bossTypeLabels: string[];
  clothes: string; // 正常 | 損傷 | 大破（その階の結果）
  climaxed: boolean;
  arousal: number; // その階の結果の発情
  sceneExcerpt: string; // [[trap]]〜[[scene-end]] の表示行を繋いだもの
}

export interface SceneImagePrompt {
  /** NAI 形式のポジティブプロンプト（カンマ区切りタグ） */
  positive: string;
  /** NAI 形式のネガティブプロンプト（カンマ区切りタグ） */
  negative: string;
  /** プロンプトの根拠にした場面テキスト抜粋（スナップショット） */
  sceneExcerpt: string;
  /** 組み立てに使った根拠の短い注記（画面表示用） */
  basis: string;
}

/** 画風・品質の固定枠（全場面共通） */
const IMAGE_QUALITY_TAGS = "masterpiece, best quality, very aesthetic, absurdres";
const IMAGE_SUBJECT_TAGS = "1girl, solo, adult woman, fantasy adventurer, dungeon explorer outfit";
const IMAGE_ENV_TAGS = "dark fantasy dungeon interior, stone corridor, torchlight, detailed background, cinematic lighting";
const IMAGE_STYLE_TAGS = "anime illustration, dramatic composition, landscape orientation";
const IMAGE_NEGATIVE_TAGS =
  "lowres, bad anatomy, bad hands, missing fingers, extra digits, fewer digits, worst quality, low quality, normal quality, jpeg artifacts, blurry, deformed, text, watermark, signature, username, logo, multiple girls, 1boy, male, loli, child, chibi, monochrome, sketch";

/** 系統ごとの場面タグ（レッテル表の系統イメージをタグ化したもの） */
const SYSTEM_SCENE_TAGS: Record<SystemKey, string> = {
  A: "pitfall trap, sticky floor, slime puddle on the floor, dungeon trap",
  B: "restrained, rope bondage, chains, bound wrists, struggling",
  C: "torn clothes, damaged outfit, wardrobe malfunction, ripped fabric",
  D: "aphrodisiac gas, pink mist, flushed face, sweating, hazy air",
  E: "monster creature, slime creature, tentacles approaching, dungeon monster",
  F: "mechanical device, machine arms, restraint device, strange machine",
  G: "glowing magic circle, hypnosis, spiral pattern, enchantment magic",
  H: "transformation magic, animal ears, tail, magic sparkles, changed body",
  I: "spotlight, stage, recording magic circle, embarrassed, being watched",
  J: "glowing crest on skin, trembling, overwhelmed, pent-up magic",
  K: "sensory deprivation, blindfold, steam, temperature change, dark room",
  L: "mimic treasure chest, cursed treasure, suspicious spring, fake reward",
  M: "tightening restraints, vines wrapping around body, escalating trap",
  N: "glowing rune mark on skin, lingering magic, marked body",
  O: "liquid metal coating, glowing pattern on skin, splattered fluid",
  P: "sound waves, giant bell, resonance, echoing corridor, vibrating air",
  Q: "repulsion barrier, sparks, slippery floor, clothes moving on their own",
  R: "ancient altar, balance scales, thorny vines, bargain with the dungeon",
  S: "reversal barrier, spring of attributes, glowing emblem, pheromone mist",
};

/** 罠名に含まれる語から拾う追加タグ（決定的キーワード対応） */
const TRAP_NAME_TAG_HINTS: [string, string][] = [
  ["スライム", "slime"],
  ["蔦", "vines"],
  ["魔法陣", "magic circle"],
  ["ガス", "gas"],
  ["霧", "mist"],
  ["花粉", "pollen, flowers"],
  ["泉", "spring water, pool"],
  ["縄", "rope"],
  ["鎖", "chains"],
  ["蜘蛛", "spider web"],
  ["椅子", "chair"],
  ["台", "platform"],
  ["鏡", "mirror"],
  ["温泉", "hot spring, steam"],
  ["網", "net"],
  ["水濡れ", "wet clothes, soaked"],
  ["酸", "acid, melting clothes"],
  ["虫", "insects"],
  ["電撃", "electricity, sparks"],
  ["電流", "electricity, sparks"],
  ["録画", "recording, camera view"],
  ["中継", "broadcast, recording"],
  ["舞台", "stage, spotlight"],
  ["ベッド", "bed"],
  ["果実", "fruit"],
  ["宝物", "treasure"],
  ["祭壇", "altar"],
  ["紋章", "crest, emblem"],
  ["刻印", "brand mark, emblem"],
  ["首輪", "collar"],
  ["手枷", "shackles"],
  ["石化", "petrification, stone statue"],
  ["胞子", "spores, mushrooms"],
  ["キノコ", "mushrooms"],
  ["暗闇", "darkness"],
  ["目隠し", "blindfold"],
  ["鐘", "bell"],
  ["茨", "thorns"],
  ["棘", "thorns"],
  ["天秤", "balance scales"],
  ["扉", "large door"],
  ["香", "incense smoke, fragrance"],
  ["夢", "dreamlike, surreal"],
  ["幻覚", "hallucination, illusion"],
  ["双子", "mirror image, twin reflection"],
];

export function buildSceneImagePrompt(input: SceneImagePromptInput): SceneImagePrompt {
  const tags: string[] = [IMAGE_QUALITY_TAGS, IMAGE_SUBJECT_TAGS];
  tags.push(SYSTEM_SCENE_TAGS[input.system]);
  const hintTags: string[] = [];
  for (const [kw, tag] of TRAP_NAME_TAG_HINTS) {
    if (input.trapName.includes(kw) && !hintTags.includes(tag)) hintTags.push(tag);
  }
  if (hintTags.length > 0) tags.push(hintTags.join(", "));
  if (input.isBoss) {
    tags.push("boss trap, grand scale trap, overwhelming contraption");
    if (input.bossTypeLabels.length > 0) tags.push(`enhanced trap (${input.bossTypeLabels.join(", ")})`);
  }
  // 体の状態（ダイジェストの結果から）
  if (input.clothes === "損傷") tags.push("torn clothes, damaged outfit");
  else if (input.clothes === "大破") tags.push("heavily torn clothes, outfit in ruins");
  if (input.climaxed) tags.push("exhausted expression, trembling body, afterglow");
  else if (input.arousal >= 60) tags.push("heavy breathing, flushed face, restless");
  else if (input.arousal >= 30) tags.push("slight blush, uneasy expression");
  tags.push(IMAGE_ENV_TAGS, IMAGE_STYLE_TAGS);
  const basisParts = [
    `罠「${input.trapName}」（${input.systemName}系統${input.isBoss ? "・ボス" : ""}）`,
    `服：${input.clothes}`,
    input.climaxed ? "絶頂あり" : `発情 ${input.arousal}`,
  ];
  return {
    positive: tags.join(", "),
    negative: IMAGE_NEGATIVE_TAGS,
    sceneExcerpt: input.sceneExcerpt,
    basis: basisParts.join("／"),
  };
}
