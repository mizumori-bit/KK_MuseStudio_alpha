import { useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { SafeAreaTopScrim } from "@hatch/space-sdk/client";
import { api, type ApiResponse } from "./api";

type Status = ApiResponse<typeof api, "get_status">;
type RunState = NonNullable<Status["run"]>;
type Digest = Status["log"][number]["digest"];
type InvItem = ApiResponse<typeof api, "list_inventory">["items"][number];
type LogEntry = Status["log"][number];
type RecordRow = ApiResponse<typeof api, "list_records">["records"][number];
type GenItem = NonNullable<Status["lastResult"]>["gained"][number];
type ResultData = NonNullable<Status["lastResult"]>;

// リプレイの対象（ライブの直近ログ、または記録の1件分をスナップショットで保持）
type ReplaySource = {
  key: string;
  runId: number;
  title: string;
  course: number;
  difficultyLabel: string;
  result: string | null; // 終了済みなら結果、進行中なら null
  gained: GenItem[];
  lost: GenItem[];
  log: LogEntry[];
};

const COURSES = [10, 20, 30];
const DIFFS = [
  { key: "easy", label: "やさしい" },
  { key: "normal", label: "ふつう" },
  { key: "hard", label: "つらい" },
] as const;

const LINE_MS = 2600; // オート送り：1行あたりの基本表示時間
const FLASH_MS = 1100; // 絶頂の明滅の長さ（この間テキスト送り不可）

// 罠シーンの表示内容：生成済みの絵があれば絵、無ければ「絵なし」の専用枠。
// 絵の所属はイベント記録（run×階）の画像スロットが持ち、タイミングは [[trap]] マーカーが担う。
type SceneVisual =
  | { kind: "image"; src: string; alt: string }
  | {
      kind: "frame";
      status: string; // none | awaiting | generating | failed（ready は image 側で扱う）
      trapName: string | null;
      systemName: string | null;
      hasPrompt: boolean;
      canGenerate: boolean;
      generateBusy: boolean;
      onGenerate: (() => void) | null;
    }
  | null;

// ---------------- 疑似3D回廊（Canvas） ----------------

function CorridorCanvas({ floor, active }: { floor: number; active: boolean }) {
  const canvasRef = useRef<HTMLCanvasElement | null>(null);
  const floorRef = useRef(floor);
  floorRef.current = floor;

  useEffect(() => {
    const canvas = canvasRef.current;
    if (!canvas) return;
    const ctx = canvas.getContext("2d");
    if (!ctx) return;
    let raf = 0;
    let depth = floorRef.current; // アニメーション中の深度
    let w = 0;
    let h = 0;
    const resize = () => {
      const rect = canvas.getBoundingClientRect();
      const dpr = Math.min(window.devicePixelRatio || 1, 2);
      w = rect.width;
      h = rect.height;
      canvas.width = Math.max(1, Math.round(w * dpr));
      canvas.height = Math.max(1, Math.round(h * dpr));
      ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    };
    resize();
    const ro = new ResizeObserver(resize);
    ro.observe(canvas);

    const segShade = (i: number, base: number) => {
      const v = Math.max(8, base - i * 5);
      return `rgb(${v + 6},${v + 2},${v - 4})`;
    };

    const draw = (t: number) => {
      // 目標深度へ滑らかに前進
      const target = floorRef.current;
      depth += (target - depth) * 0.045;
      if (Math.abs(target - depth) < 0.001) depth = target;
      const frac = depth - Math.floor(depth);
      const baseSeg = Math.floor(depth);

      ctx.clearRect(0, 0, w, h);
      ctx.fillStyle = "#0a0908";
      ctx.fillRect(0, 0, w, h);
      const vx = w * 0.5;
      const vy = h * 0.47;
      const SEG = 8;

      const frameAt = (k: number) => {
        // k: 0=手前 … SEG=最奥。frac で手前へ流れる
        const z = k - frac;
        const s = 1 / (1 + Math.max(0, z) * 0.62);
        const hw = (w * 0.52) * s;
        const hh = (h * 0.56) * s;
        return { x0: vx - hw, x1: vx + hw, y0: vy - hh, y1: vy + hh, s };
      };

      // 奥から手前へ壁・床・天井を描く
      for (let k = SEG; k >= 1; k--) {
        const a = frameAt(k);
        const b = frameAt(k - 1);
        const idx = baseSeg + k;
        // 天井
        ctx.fillStyle = segShade(k, 34);
        ctx.beginPath();
        ctx.moveTo(a.x0, a.y0); ctx.lineTo(a.x1, a.y0); ctx.lineTo(b.x1, b.y0); ctx.lineTo(b.x0, b.y0);
        ctx.closePath(); ctx.fill();
        // 床
        ctx.fillStyle = segShade(k, 44);
        ctx.beginPath();
        ctx.moveTo(a.x0, a.y1); ctx.lineTo(a.x1, a.y1); ctx.lineTo(b.x1, b.y1); ctx.lineTo(b.x0, b.y1);
        ctx.closePath(); ctx.fill();
        // 左壁
        ctx.fillStyle = segShade(k, 52);
        ctx.beginPath();
        ctx.moveTo(a.x0, a.y0); ctx.lineTo(b.x0, b.y0); ctx.lineTo(b.x0, b.y1); ctx.lineTo(a.x0, a.y1);
        ctx.closePath(); ctx.fill();
        // 右壁
        ctx.fillStyle = segShade(k, 48);
        ctx.beginPath();
        ctx.moveTo(a.x1, a.y0); ctx.lineTo(b.x1, b.y0); ctx.lineTo(b.x1, b.y1); ctx.lineTo(a.x1, a.y1);
        ctx.closePath(); ctx.fill();
        // 石の継ぎ目（横線）
        ctx.strokeStyle = "rgba(0,0,0,0.55)";
        ctx.lineWidth = Math.max(1, 2.2 * a.s);
        const rows = 3;
        for (let r = 1; r <= rows; r++) {
          const ly = a.y0 + ((a.y1 - a.y0) * r) / (rows + 1);
          const ry = a.y0 + ((a.y1 - a.y0) * r) / (rows + 1);
          ctx.beginPath(); ctx.moveTo(a.x0, ly); ctx.lineTo(b.x0, ly + (b.y0 - a.y0) * 0); ctx.stroke();
          ctx.beginPath(); ctx.moveTo(a.x1, ry); ctx.lineTo(b.x1, ry); ctx.stroke();
        }
        // 枠（柱）
        ctx.strokeStyle = "rgba(0,0,0,0.8)";
        ctx.lineWidth = Math.max(1, 3.4 * a.s);
        ctx.strokeRect(a.x0, a.y0, a.x1 - a.x0, a.y1 - a.y0);
        ctx.strokeStyle = "rgba(201,162,39,0.10)";
        ctx.lineWidth = 1;
        ctx.strokeRect(a.x0 + 2, a.y0 + 2, a.x1 - a.x0 - 4, a.y1 - a.y0 - 4);

        // 松明（2セグメントごとに左右交互）
        if (idx % 2 === 0 && k <= SEG - 1) {
          const side = idx % 4 === 0 ? -1 : 1;
          const tx = side < 0 ? a.x0 + (b.x0 - a.x0) * 0.4 : a.x1 + (b.x1 - a.x1) * 0.4;
          const ty = a.y0 + (a.y1 - a.y0) * 0.34;
          const fs = Math.max(2, 15 * a.s);
          const flick = 1 + Math.sin(t / 90 + idx * 1.7) * 0.16 + Math.sin(t / 37 + idx) * 0.08;
          const glow = ctx.createRadialGradient(tx, ty, 0, tx, ty, fs * 5.2 * flick);
          glow.addColorStop(0, "rgba(255,150,50,0.34)");
          glow.addColorStop(1, "rgba(255,120,30,0)");
          ctx.fillStyle = glow;
          ctx.fillRect(tx - fs * 5.2, ty - fs * 5.2, fs * 10.4, fs * 10.4);
          ctx.fillStyle = "#2c1c08";
          ctx.fillRect(tx - fs * 0.09, ty, fs * 0.18, fs * 0.9);
          ctx.fillStyle = `rgba(255,${140 + Math.round(40 * flick)},40,0.95)`;
          ctx.beginPath();
          ctx.ellipse(tx, ty - fs * 0.28 * flick, fs * 0.30, fs * 0.55 * flick, 0, 0, Math.PI * 2);
          ctx.fill();
          ctx.fillStyle = "rgba(255,230,160,0.9)";
          ctx.beginPath();
          ctx.ellipse(tx, ty - fs * 0.18 * flick, fs * 0.14, fs * 0.26 * flick, 0, 0, Math.PI * 2);
          ctx.fill();
        }
      }
      // 最奥の闇
      const end = frameAt(SEG);
      const eg = ctx.createRadialGradient(vx, vy, 0, vx, vy, Math.max(end.x1 - end.x0, 30));
      eg.addColorStop(0, "rgba(0,0,0,0.96)");
      eg.addColorStop(1, "rgba(0,0,0,0)");
      ctx.fillStyle = eg;
      ctx.fillRect(end.x0 - 40, end.y0 - 40, end.x1 - end.x0 + 80, end.y1 - end.y0 + 80);
      // 周辺減光
      const vg = ctx.createRadialGradient(w / 2, h / 2, Math.min(w, h) * 0.36, w / 2, h / 2, Math.max(w, h) * 0.78);
      vg.addColorStop(0, "rgba(0,0,0,0)");
      vg.addColorStop(1, "rgba(0,0,0,0.55)");
      ctx.fillStyle = vg;
      ctx.fillRect(0, 0, w, h);
      if (!active) {
        ctx.fillStyle = "rgba(0,0,0,0.45)";
        ctx.fillRect(0, 0, w, h);
      }
      raf = requestAnimationFrame(draw);
    };
    raf = requestAnimationFrame(draw);
    return () => {
      cancelAnimationFrame(raf);
      ro.disconnect();
    };
  }, [active]);

  return <canvas ref={canvasRef} className="h-full w-full" aria-label="ダンジョン回廊の疑似3D表示" />;
}

/**
 * 「絵なし」の専用枠。画像の読み込み失敗（壊れた画像アイコン）とは見た目をはっきり分け、
 * 絵がないこと自体を明示する。生成リクエストの保存状態もここに出す。
 */
function NoImageFrame({ v }: { v: Extract<NonNullable<SceneVisual>, { kind: "frame" }> }) {
  const statusLine =
    v.status === "generating"
      ? "生成中です。届き次第、この場面の絵に差し替わります。"
      : v.status === "failed"
        ? "前回の生成は失敗しました。記録とプロンプトは残っているので、再試行できます。"
        : v.hasPrompt
          ? "この場面の生成リクエスト（プロンプト）は記録済みです。あとから生成できます。"
          : "この場面には、まだ生成リクエストが記録されていません（記録・リプレイの画面から作成できます）。";
  return (
    <div className="flex h-full w-full items-center justify-center bg-[#171310] p-4">
      <div className="w-full max-w-md border border-dashed border-[var(--accent-dim)] bg-black/55 px-5 py-5 text-center">
        <p className="font-serif-jp text-lg text-[var(--text)]">この場面の絵は、まだありません</p>
        {v.trapName && (
          <p className="mt-1.5 text-xs text-[var(--dim)]">
            {v.systemName ?? ""}系統の罠「{v.trapName}」の場面です
          </p>
        )}
        <p className="mt-2.5 text-sm leading-relaxed text-[var(--dim)]">{statusLine}</p>
        <p className="mt-2 text-[11px] leading-relaxed text-[var(--dim)]">
          ※ これは「絵がない」状態を示す表示です。画像の読み込み失敗や、表示の不具合ではありません。
        </p>
        {v.canGenerate && v.onGenerate && (
          <button
            type="button"
            disabled={v.generateBusy}
            onClick={v.onGenerate}
            className="mt-3.5 border border-[var(--accent)] bg-[var(--accent)] px-4 py-2 text-sm font-semibold text-black disabled:opacity-40"
          >
            {v.status === "failed" ? "生成を再試行する" : "保存済みプロンプトで絵を生成する"}
          </button>
        )}
        {v.status === "generating" && <p className="mt-3 text-xs text-[var(--accent)]">生成中…</p>}
      </div>
    </div>
  );
}

/**
 * ステージ表示層：通常は疑似3D回廊。
 * 罠シーンに入ったら、回廊からその場面の表示（生成済みの絵／「絵なし」枠）へ
 * クロスフェードで遷移し、場面を抜ければ回廊へ戻る。
 * 入り側もフェードさせるため、内容はまず透明（scene-hidden）でマウントし、
 * 次フレームで透明を解除してトランジションを発火させる
 * （不透明な状態でマウントすると、入りだけトランジションが効かない）。
 * 「絵なし」枠の状態変化（未生成→生成中 等）はフェードし直さず中身だけ差し替える。
 */
function SceneStage({
  floor,
  active,
  visual,
}: {
  floor: number;
  active: boolean;
  visual: SceneVisual;
}) {
  // 表示中の内容（フェードアウト完了まで保持）と、フェードイン済みかどうか
  const [shown, setShown] = useState<SceneVisual>(null);
  const [entered, setEntered] = useState(false);
  const shownKeyRef = useRef<string | null>(null);
  useEffect(() => {
    const k = visual == null ? null : visual.kind === "image" ? `img:${visual.src}` : "frame";
    if (visual) {
      if (shownKeyRef.current !== k) {
        shownKeyRef.current = k;
        setShown(visual);
        setEntered(false);
        // 透明でマウントされた次フレームで不透明へ → 入り側のクロスフェードが走る
        let raf2 = 0;
        const raf1 = requestAnimationFrame(() => {
          raf2 = requestAnimationFrame(() => setEntered(true));
        });
        return () => {
          cancelAnimationFrame(raf1);
          if (raf2) cancelAnimationFrame(raf2);
        };
      }
      // 同じ枠のまま状態だけ変わった（生成中→生成済み等）：フェードし直さず差し替え
      setShown(visual);
      setEntered(true);
      return undefined;
    }
    // 内容が消えたらフェードアウト後にアンマウント
    setEntered(false);
    const id = setTimeout(() => {
      shownKeyRef.current = null;
      setShown(null);
    }, 950);
    return () => clearTimeout(id);
  }, [visual]);
  return (
    <div className="absolute inset-0">
      <div className={`scene-crossfade absolute inset-0 ${entered ? "scene-hidden" : ""}`}>
        <CorridorCanvas floor={floor} active={active} />
      </div>
      {shown?.kind === "image" && (
        <img
          src={shown.src}
          alt={shown.alt}
          className={`scene-crossfade absolute inset-0 h-full w-full object-cover ${entered ? "" : "scene-hidden"}`}
        />
      )}
      {shown?.kind === "frame" && (
        <div className={`scene-crossfade absolute inset-0 ${entered ? "" : "scene-hidden"}`}>
          <NoImageFrame v={shown} />
        </div>
      )}
    </div>
  );
}

// ---------------- 小物 ----------------

function RarityBadge({ rarity }: { rarity: number }) {
  return (
    <span
      className="inline-flex h-5 min-w-5 items-center justify-center border border-[var(--accent-dim)] px-1 text-xs font-semibold text-[var(--accent)]"
      aria-label={`レアリティ ${rarity}`}
    >
      {rarity}
    </span>
  );
}

function ItemLine({ item }: { item: { name: string; systemName?: string; system: string; bonus: number; rarity: number } }) {
  return (
    <span className="inline-flex flex-wrap items-center gap-x-2 gap-y-0.5">
      <RarityBadge rarity={item.rarity} />
      <span className="font-serif-jp">{item.name}</span>
      <span className="text-xs text-[var(--dim)]">
        対{item.systemName ?? item.system} 回避+{item.bonus}
      </span>
    </span>
  );
}

function DigestView({ d }: { d: Digest }) {
  return (
    <div>
      <p className="text-xs tracking-wide text-[var(--accent)]">
        {d.floor}階層{d.isBoss ? " ― ボストラップ" : d.kind === "quiet" ? " ― 静寂" : d.kind === "chest" ? " ― 宝箱" : " ― 罠"}
      </p>
      <p className="font-serif-jp mt-1 leading-relaxed">{d.summary}</p>
      <p className="mt-1 text-sm text-[var(--dim)]">{d.labelText}</p>
      <div className="mt-1.5 flex flex-wrap gap-x-3 gap-y-1 text-xs text-[var(--dim)]">
        {d.avoidChance != null && (
          <span>
            回避判定：成功率{d.avoidChance}% → {d.avoided ? "回避成功" : "発動"}
          </span>
        )}
        {d.avoidChance == null && d.isBoss && <span>回避判定なし（不可避化）</span>}
        {d.arousalDelta !== 0 && (
          <span>
            発情 {d.arousalDelta > 0 ? "+" : ""}
            {d.arousalDelta} → {d.arousal}
          </span>
        )}
        {d.climaxed && <span className="text-[var(--danger)]">絶頂した（累計{d.climaxCount}回）</span>}
        {d.consumedItemName && <span>代償として「{d.consumedItemName}」を消費</span>}
        {d.chainTrapName && <span>連鎖：「{d.chainTrapName}」の効果も適用</span>}
        {d.itemGained && (
          <span className="text-[var(--accent)]">
            仮保持：{d.itemGained.name}（レアリティ{d.itemGained.rarity}）
          </span>
        )}
        {d.runFinished && (
          <span className="text-[var(--danger)]">
            {d.finishKind === "clear" ? "最終ボスを越えた ― 文章の提出でクリア確定" : "絶頂3回 ― 文章の提出で敗北確定"}
          </span>
        )}
      </div>
    </div>
  );
}

const IMAGE_STATUS_LABEL: Record<string, string> = {
  none: "生成リクエスト未作成",
  awaiting: "プロンプト保存済み・未生成",
  generating: "生成中",
  ready: "生成済み",
  failed: "生成失敗（再試行できます）",
};

/** 罠シーンの画像スロット表示（記録・バックログ用）。対象外の階では何も出さない */
function ImageSlotInfo({
  entry,
  onGenerate,
  busy,
}: {
  entry: LogEntry;
  onGenerate?: (floor: number) => void;
  busy?: boolean;
}) {
  const d = entry.digest;
  if (!((d.kind === "trap" || d.isBoss) && d.avoided === false)) return null;
  const img = entry.image;
  return (
    <div className="mt-1.5 border border-[var(--border)] bg-black/30 px-3 py-2 text-xs">
      <p className="text-[var(--dim)]">
        場面の絵：
        <span className={img.status === "ready" ? "text-[var(--accent)]" : undefined}>
          {IMAGE_STATUS_LABEL[img.status] ?? img.status}
        </span>
        {img.prompt && <span>（根拠：{img.prompt.basis}）</span>}
      </p>
      {img.status === "ready" && img.imageUrl && (
        <img
          src={img.imageUrl}
          alt={`${d.systemName ?? ""}系統の罠「${d.trapName ?? ""}」の場面の絵`}
          className="mt-2 max-h-60 w-full object-cover"
        />
      )}
      {img.prompt && (
        <details className="mt-1.5">
          <summary className="cursor-pointer text-[var(--dim)]">生成リクエスト（プロンプト）を見る</summary>
          <div className="mt-1.5 space-y-1.5 break-words leading-relaxed">
            <p>
              <span className="text-[var(--dim)]">ポジティブ：</span>
              {img.prompt.positive}
            </p>
            <p>
              <span className="text-[var(--dim)]">ネガティブ：</span>
              {img.prompt.negative}
            </p>
            {img.prompt.sceneExcerpt && (
              <p>
                <span className="text-[var(--dim)]">場面抜粋：</span>
                {img.prompt.sceneExcerpt}
              </p>
            )}
          </div>
        </details>
      )}
      {onGenerate && (img.status === "awaiting" || img.status === "failed") && (
        <button
          type="button"
          disabled={busy}
          onClick={() => onGenerate(entry.floor)}
          className="mt-2 border border-[var(--accent-dim)] px-3 py-1.5 text-xs text-[var(--accent)] disabled:opacity-40"
        >
          {img.status === "failed" ? "生成を再試行する" : "保存済みプロンプトで絵を生成する"}
        </button>
      )}
    </div>
  );
}

/** テキストボックス上部に出す、その階の機械情報（ラベル＋判定結果）。
 *  常に1行に収め（はみ出しは省略）、テキストボックスの高さが揺れないようにする。
 *  絶頂の表示は階到着時ではなく、文章中の絶頂の瞬間を通過してから出す
 *  （climaxCountShown が null の間は出さない）。 */
function DigestCaption({ d, climaxCountShown }: { d: Digest; climaxCountShown?: number | null }) {
  return (
    <div className="flex items-baseline gap-x-3 overflow-hidden whitespace-nowrap">
      <span className="shrink-0 text-xs tracking-wide text-[var(--accent)]">
        {d.floor}階層{d.isBoss ? " ― ボストラップ" : d.kind === "quiet" ? " ― 静寂" : d.kind === "chest" ? " ― 宝箱" : " ― 罠"}
      </span>
      <span className="min-w-0 truncate text-xs text-[var(--dim)]">{d.labelText}</span>
      {d.avoidChance != null && (
        <span className="shrink-0 text-xs text-[var(--dim)]">
          回避 {d.avoidChance}% → {d.avoided ? "成功" : "発動"}
        </span>
      )}
      {d.avoidChance == null && d.isBoss && <span className="shrink-0 text-xs text-[var(--dim)]">回避判定なし（不可避化）</span>}
      {climaxCountShown != null && <span className="shrink-0 text-xs text-[var(--danger)]">絶頂した（累計{climaxCountShown}回）</span>}
      {d.itemGained && <span className="shrink-0 text-xs text-[var(--accent)]">仮保持：{d.itemGained.name}</span>}
      {d.consumedItemName && <span className="shrink-0 text-xs text-[var(--dim)]">代償：「{d.consumedItemName}」を消費</span>}
      {d.chainTrapName && <span className="shrink-0 text-xs text-[var(--dim)]">連鎖：「{d.chainTrapName}」</span>}
    </div>
  );
}

// ---------------- ステータス（ステージ右上） ----------------

type StatusLike = {
  climaxes: number;
  climaxLimit: number;
  arousal: number;
  sensitivity: number;
  clothes: string;
  canReturn: boolean;
  returnFloor: number;
  pendingCount: number;
  effects: { label: string; turnsLeft: number }[];
  sub: string;
};

function StatusPanel({ s }: { s: StatusLike }) {
  return (
    <section
      aria-label="ステータス"
      className="gold-frame pointer-events-auto w-44 max-w-[52vw] bg-black/60 p-3 backdrop-blur-[2px] sm:w-60"
    >
      <div className="flex items-baseline justify-between gap-2">
        <p className="text-[11px] tracking-widest text-[var(--dim)]">ステータス</p>
        <p className="truncate text-[11px] text-[var(--dim)]">{s.sub}</p>
      </div>
      <div className="mt-2">
        <div className="flex justify-between text-[11px] text-[var(--dim)]">
          <span>絶頂</span>
          <span>{s.climaxes} / {s.climaxLimit} 回</span>
        </div>
        <div className="mt-1 flex gap-1.5" aria-label={`絶頂 ${s.climaxes} 回`}>
          {Array.from({ length: s.climaxLimit }).map((_, i) => (
            <span key={i} className={`h-2 flex-1 ${i < s.climaxes ? "bg-[var(--danger)]" : "bg-white/15"}`} />
          ))}
        </div>
      </div>
      <div className="mt-2">
        <div className="flex justify-between text-[11px] text-[var(--dim)]">
          <span>発情</span>
          <span>{Math.round(s.arousal)} / 100</span>
        </div>
        <div
          className="mt-1 h-2.5 w-full bg-white/15"
          role="progressbar"
          aria-label="発情ゲージ"
          aria-valuenow={Math.round(s.arousal)}
          aria-valuemin={0}
          aria-valuemax={100}
        >
          <div
            className="h-full bg-gradient-to-r from-[#7a4a56] to-[var(--danger)] transition-all duration-700"
            style={{ width: `${Math.min(100, Math.max(0, s.arousal))}%` }}
          />
        </div>
      </div>
      <dl className="mt-2 grid grid-cols-2 gap-x-2 gap-y-1 text-xs">
        <dt className="text-[var(--dim)]">感度</dt>
        <dd className="text-right">×{s.sensitivity.toFixed(1)}</dd>
        <dt className="text-[var(--dim)]">服の状態</dt>
        <dd className="text-right">{s.clothes}</dd>
        <dt className="text-[var(--dim)]">帰還</dt>
        <dd className="text-right">{s.canReturn ? <span className="text-[var(--accent)]">可能</span> : `${s.returnFloor}階から`}</dd>
        <dt className="text-[var(--dim)]">仮保持</dt>
        <dd className="text-right">{s.pendingCount} 件</dd>
      </dl>
      {s.effects.length > 0 && (
        <ul className="mt-2 flex flex-wrap gap-1">
          {s.effects.map((e, i) => (
            <li key={i} className="border border-[var(--border)] bg-black/50 px-1.5 py-0.5 text-[11px]">
              {e.label}（残り{e.turnsLeft}T）
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}

// ---------------- 中央通知 ----------------

function CenterNotice({ text, visible }: { text: string; visible: boolean }) {
  return (
    <div
      aria-hidden={!visible}
      className={`pointer-events-none absolute inset-0 z-20 flex items-center justify-center px-4 transition-opacity duration-700 ${
        visible ? "opacity-100" : "opacity-0"
      }`}
    >
      <p className="gold-frame font-serif-jp max-w-xl bg-black/75 px-5 py-3 text-center text-base leading-relaxed text-[var(--text)] sm:text-lg">
        {text}
      </p>
    </div>
  );
}

// ---------------- ポップアップ（タブ／テスト操作） ----------------

function Popup({
  title,
  onClose,
  children,
  wide,
}: {
  title: string;
  onClose: () => void;
  children: ReactNode;
  wide?: boolean;
}) {
  return (
    <div className="fixed inset-0 z-50 flex items-start justify-center overflow-y-auto bg-black/70 p-3 sm:items-center" role="dialog" aria-modal="true" aria-label={title}>
      <div className="absolute inset-0" onClick={onClose} aria-hidden="true" />
      <div className={`gold-frame relative my-auto w-full ${wide ? "max-w-3xl" : "max-w-xl"} bg-[var(--surface)]`}>
        <div className="flex items-center justify-between border-b border-[var(--border)] px-4 py-2.5">
          <p className="font-serif-jp text-[var(--accent)]">{title}</p>
          <button
            type="button"
            onClick={onClose}
            aria-label={`${title}を閉じる`}
            className="border border-[var(--border)] px-3 py-1 text-xs text-[var(--dim)]"
          >
            閉じる
          </button>
        </div>
        <div className="max-h-[78dvh] overflow-y-auto p-4">{children}</div>
      </div>
    </div>
  );
}

// ---------------- 本体 ----------------

type PopupKind = "equip" | "records" | "params" | "test" | null;

export function App() {
  const queryClient = useQueryClient();
  const status = useQuery({
    queryKey: ["status"],
    queryFn: () => api.get_status({}),
    refetchInterval: 4000,
  });
  const inventory = useQuery({ queryKey: ["inventory"], queryFn: () => api.list_inventory({}) });
  const records = useQuery({ queryKey: ["records"], queryFn: () => api.list_records({ limit: 20 }) });
  const params = useQuery({ queryKey: ["params"], queryFn: () => api.get_params({}) });

  const [popup, setPopup] = useState<PopupKind>(null);
  const [course, setCourse] = useState(10);
  const [difficulty, setDifficulty] = useState<"easy" | "normal" | "hard">("normal");
  const [selectedEquip, setSelectedEquip] = useState<number[]>([]);
  const [draftText, setDraftText] = useState("");
  const [actionError, setActionError] = useState<string | null>(null);

  // 再生（ライブ／リプレイ共通のVN再生状態）
  const [replay, setReplay] = useState<ReplaySource | null>(null);
  const [replayStep, setReplayStep] = useState(0);
  const [replaySpeed, setReplaySpeed] = useState(1);
  const [lineIdx, setLineIdx] = useState(0);
  const [auto, setAuto] = useState(false);
  const [resultShown, setResultShown] = useState(false);
  // リプレイで再生済み（到達済み）の最大ステップ。バックログ等の見返しはここまで
  const [maxReached, setMaxReached] = useState(0);
  // バックログ（ホイール上回転で開くログ表示）
  const [backlogOpen, setBacklogOpen] = useState(false);
  const backlogRef = useRef<HTMLDivElement | null>(null);
  // 起動直後（潜行なし）はリプレイ選択画面。潜行が目の前で終わった時だけリザルト画面へ
  const [pickerForced, setPickerForced] = useState(true);

  // 中央通知（noticeActive の間は本文テキストを表示しない）
  const [notice, setNotice] = useState<{ text: string; visible: boolean }>({ text: "", visible: false });
  const [noticeActive, setNoticeActive] = useState(false);
  const noticeTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  const noticeDoneRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  const lineAreaRef = useRef<HTMLDivElement | null>(null);

  const run: RunState | null = status.data?.run ?? null;
  const log = status.data?.log ?? [];
  const lastResult: ResultData | null = status.data?.lastResult ?? null;
  const hasFinishedLatest =
    !run && lastResult != null && status.data != null && status.data.logRunId === lastResult.runId;

  // 潜行中 → 終了 の遷移を検出したらリザルト表示モードへ
  const wasRunningRef = useRef(false);
  useEffect(() => {
    if (run) {
      wasRunningRef.current = true;
    } else if (wasRunningRef.current && hasFinishedLatest) {
      wasRunningRef.current = false;
      setPickerForced(false);
    }
  }, [run, hasFinishedLatest]);

  const mode: "replay" | "live" | "result" | "picker" = replay
    ? "replay"
    : run
      ? "live"
      : hasFinishedLatest && !pickerForced
        ? "result"
        : "picker";

  // ---------- 場面（シーン）の決定 ----------
  const scenes: LogEntry[] = replay ? replay.log : log;
  // バックログで見返せる範囲：リプレイでは再生済み（到達済み）の階まで。ライブは全て可
  const backlogScenes: LogEntry[] = replay
    ? scenes.slice(0, Math.min(scenes.length, maxReached + 1))
    : scenes;
  // 記録ポップアップ内のログも、再生中のリプレイについては再生済みの範囲だけ見せる
  const recordVisibleLog = (rec: RecordRow): LogEntry[] =>
    replay && replay.key === `record-${rec.runId}`
      ? rec.log.slice(0, Math.min(rec.log.length, maxReached + 1))
      : rec.log;
  const resultData: ResultData | null = replay
    ? replay.result
      ? {
          runId: 0,
          result: replay.result,
          course: replay.course,
          difficultyLabel: replay.difficultyLabel,
          floorReached: replay.log[replay.log.length - 1]?.digest.floor ?? replay.log.length,
          climaxes: replay.log[replay.log.length - 1]?.digest.climaxCount ?? 0,
          gained: replay.gained,
          lost: replay.lost,
        }
      : null
    : mode === "result"
      ? lastResult
      : null;
  const totalSteps = scenes.length + (resultData ? 1 : 0);
  const index = replay ? Math.min(replayStep, Math.max(0, totalSteps - 1)) : Math.max(0, scenes.length - 1);
  const atResult = resultData != null && (replay ? index >= scenes.length : mode === "result");
  const entry: LogEntry | null = !atResult && scenes.length > 0 ? (scenes[index] ?? null) : null;
  const lastDigest: Digest | null = scenes[scenes.length - 1]?.digest ?? null;
  const curDigest: Digest | null = entry?.digest ?? (atResult ? lastDigest : null);

  // ---------- テキスト行送り ----------
  const lines = useMemo(
    () =>
      entry?.text
        ? entry.text.split(/\r?\n/).map((s) => s.trim()).filter((s) => s.length > 0)
        : [],
    [entry?.text],
  );
  const lineDone = lineIdx >= lines.length;
  const displayLine = lines.length > 0 ? (lines[Math.min(lineIdx, lines.length - 1)] ?? "") : "";

  const sceneKey = `${mode}:${replay?.key ?? status.data?.logRunId ?? 0}:${index}:${entry?.text ? 1 : 0}:${atResult ? 1 : 0}`;
  const prevSceneKeyRef = useRef(sceneKey);
  useEffect(() => {
    if (prevSceneKeyRef.current !== sceneKey) {
      prevSceneKeyRef.current = sceneKey;
      setLineIdx(0);
      setResultShown(false);
    }
  }, [sceneKey]);

  // リプレイで到達した最大ステップを記録する（見返せる範囲の上限）
  useEffect(() => {
    if (replay) setMaxReached((m) => Math.max(m, replayStep));
  }, [replay, replayStep]);

  // 行が変わったら本文エリアの内部スクロールを先頭へ戻す
  useEffect(() => {
    const el = lineAreaRef.current;
    if (el) el.scrollTop = 0;
  }, [lineIdx, sceneKey]);

  // ---------- 文章内マーカーによる演出タイミング ----------
  // マーカーは「その行を表示した時点」で通過扱い。真実はダイジェスト側が決め、
  // マーカーはタイミング専用（過剰分は無視、不足分は文章の終わりで適用）。
  const marks = entry?.marks ?? [];
  const prevDigest: Digest | null = index > 0 ? (scenes[index - 1]?.digest ?? null) : null;
  const climaxDeltaF =
    entry && curDigest ? Math.max(0, curDigest.climaxDelta || (curDigest.climaxed ? 1 : 0)) : 0;
  const climaxMarksPassed = marks.filter((m) => m.type === "climax" && lineIdx >= m.atLine).length;
  const climaxPassed = entry ? (lineDone ? climaxDeltaF : Math.min(climaxMarksPassed, climaxDeltaF)) : 0;
  const climaxesShown = !curDigest
    ? 0
    : entry
      ? (prevDigest?.climaxCount ?? 0) + climaxPassed
      : curDigest.climaxCount;

  // 罠シーン（絵）に入っているか：[[trap]] を通過し、[[scene-end]] を通過しておらず、文章が終わっていない間
  const trapHit =
    !!entry && (entry.digest.kind === "trap" || entry.digest.isBoss) && entry.digest.avoided === false;
  const trapMark = marks.find((m) => m.type === "trap");
  const trapAt = trapMark ? trapMark.atLine : null;
  const trapFired =
    trapHit && (trapAt != null ? lineIdx >= trapAt : lines.length === 0 || lineIdx >= lines.length - 1);
  const sceneEndFired =
    trapAt != null && marks.some((m) => m.type === "scene-end" && m.atLine >= trapAt && lineIdx >= m.atLine);
  const inTrapScene = trapFired && !sceneEndFired && !lineDone;

  // 発情の表示タイミング：階に到着した時点では前階の値を保持し、その階の出来事が
  // 文章中で示された時点で初めて増減を反映する（到着時点で増えると、その階で
  // 罠に当たることが文章を読む前に分かってしまう＝ネタバレになるため）。
  // 罠に当たった階は [[trap]] の通過時点（マーカーが無ければ最終行）で増え、
  // 静かな階は文章を読み終えた時点で下がる。絶頂のある階は、罠の時点から
  // 絶頂の瞬間までは絶頂直前のピークを保持し、絶頂を通過した時点で絶頂後の値へ落とす。
  // 文章待ち（テキスト未提出）の間も前階の値のまま。
  const arousalShown = ((): number => {
    if (!curDigest) return 0;
    if (!entry) return curDigest.arousal; // リザルト表示：最終値をそのまま出す
    const prevArousal = prevDigest?.arousal ?? 0;
    if (entry.text == null) return prevArousal;
    if (trapHit) {
      if (!trapFired) return prevArousal;
      if (climaxDeltaF > 0 && climaxPassed < climaxDeltaF) {
        return curDigest.arousalPeak ?? curDigest.arousal;
      }
      return curDigest.arousal;
    }
    if (curDigest.kind === "quiet" && !lineDone) return prevArousal;
    return curDigest.arousal;
  })();

  // 絶頂の明滅：絶頂の瞬間を通過したら発火し、終わるまでテキスト送りを止める
  const [flashKey, setFlashKey] = useState(0);
  const [flashing, setFlashing] = useState(false);
  const flashTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  const prevClimaxRef = useRef<{ key: string; count: number }>({ key: "", count: 0 });
  useEffect(() => {
    const prev = prevClimaxRef.current;
    prevClimaxRef.current = { key: sceneKey, count: climaxPassed };
    if (prev.key === sceneKey && climaxPassed > prev.count) {
      if (flashTimerRef.current) clearTimeout(flashTimerRef.current);
      setFlashKey((k) => k + 1);
      setFlashing(true);
      flashTimerRef.current = setTimeout(() => setFlashing(false), FLASH_MS);
    }
  }, [sceneKey, climaxPassed]);
  useEffect(
    () => () => {
      if (flashTimerRef.current) clearTimeout(flashTimerRef.current);
    },
    [],
  );

  // ---------- 中央通知（場面が変わったら約3秒表示） ----------
  // 表示中（フェードアウト完了まで）は noticeActive=true で、本文テキストは出さない。
  // タイマーはrefで一元管理し、再取得でcurDigestの同一性が変わっても消し忘れない。
  const noticeKey = curDigest && !atResult ? `${mode}:${index}:${curDigest.floor}:${curDigest.summary}` : "";
  const prevNoticeKeyRef = useRef("");
  const noticeDismissedRef = useRef(false);
  useEffect(() => {
    if (!noticeKey || !curDigest || mode === "picker") return;
    if (prevNoticeKeyRef.current === noticeKey) return;
    prevNoticeKeyRef.current = noticeKey;
    noticeDismissedRef.current = false;
    if (noticeTimerRef.current) clearTimeout(noticeTimerRef.current);
    if (noticeDoneRef.current) clearTimeout(noticeDoneRef.current);
    setNotice({ text: curDigest.summary, visible: true });
    setNoticeActive(true);
    noticeTimerRef.current = setTimeout(() => setNotice((n) => ({ ...n, visible: false })), 2600);
    noticeDoneRef.current = setTimeout(() => setNoticeActive(false), 3350);
  }, [noticeKey, mode]); // eslint-disable-line react-hooks/exhaustive-deps

  // ポップアップを早めに閉じる（クリック／キーでテキスト送りが押された時）。
  // 最初のクリックで終了時刻を確定させる。連打でタイマーを延長し続けない
  // （延長されると、速くクリックする限りテキストがいつまでも出てこない）。
  const dismissNotice = () => {
    if (noticeDismissedRef.current) return;
    noticeDismissedRef.current = true;
    if (noticeTimerRef.current) clearTimeout(noticeTimerRef.current);
    if (noticeDoneRef.current) clearTimeout(noticeDoneRef.current);
    setNotice((n) => ({ ...n, visible: false }));
    noticeDoneRef.current = setTimeout(() => setNoticeActive(false), 420);
  };

  // ---------- オート送り ----------
  const resultPending = resultData != null && (mode === "result" ? lineDone : atResult) && !resultShown;
  useEffect(() => {
    if (!auto || resultShown || popup || backlogOpen || noticeActive || flashing) return;
    if (!lineDone) {
      const id = setTimeout(() => setLineIdx((i) => Math.min(i + 1, lines.length)), LINE_MS / replaySpeed);
      return () => clearTimeout(id);
    }
    if (replay && !atResult) {
      const id = setTimeout(
        () => setReplayStep((s) => Math.min(s + 1, totalSteps - 1)),
        1200 / replaySpeed,
      );
      return () => clearTimeout(id);
    }
    return undefined;
  }, [auto, lineDone, lines.length, replay, atResult, resultShown, totalSteps, replaySpeed, popup, backlogOpen, noticeActive, flashing]);

  // ---------- ミューテーション ----------
  const invalidate = () => {
    void queryClient.invalidateQueries({ queryKey: ["status"] });
    void queryClient.invalidateQueries({ queryKey: ["inventory"] });
    void queryClient.invalidateQueries({ queryKey: ["records"] });
  };
  const wrap = <T,>(fn: (v: T) => Promise<{ ok: boolean; error: string | null }>) =>
    async (v: T) => {
      const r = await fn(v);
      if (!r.ok) setActionError(r.error ?? "失敗しました");
      else setActionError(null);
      return r;
    };
  const startMut = useMutation({
    mutationFn: wrap(() => api.start_run({ course, difficulty })),
    onSuccess: () => {
      setSelectedEquip([]);
      setPopup(null);
      invalidate();
    },
  });
  const equipMut = useMutation({
    mutationFn: wrap((ids: number[]) => api.set_equip({ item_ids: ids })),
    onSuccess: invalidate,
  });
  const advanceMut = useMutation({
    mutationFn: wrap(() => api.advance({})),
    onSuccess: invalidate,
  });
  const submitMut = useMutation({
    mutationFn: wrap((text: string) => api.submit_text({ text })),
    onSuccess: () => {
      setDraftText("");
      invalidate();
    },
  });
  const returnMut = useMutation({
    mutationFn: wrap(() => api.return_home({})),
    onSuccess: invalidate,
  });
  const abandonMut = useMutation({
    mutationFn: wrap(() => api.abandon_run({})),
    onSuccess: invalidate,
  });
  // 罠シーンの絵：保存済みプロンプトから後生成する／未作成プロンプトをまとめて作る
  const genImgMut = useMutation({
    mutationFn: async (v: { runId: number; floor: number }) => {
      const r = await api.generate_event_image({ run_id: v.runId, floor: v.floor });
      if (!r.ok) setActionError(r.error ?? "画像生成に失敗しました");
      else setActionError(null);
      return r;
    },
    onSuccess: invalidate,
  });
  const preparePromptsMut = useMutation({
    mutationFn: () => api.prepare_image_prompts({}),
    onSuccess: invalidate,
  });
  const busy =
    startMut.isPending || equipMut.isPending || advanceMut.isPending ||
    submitMut.isPending || returnMut.isPending || abandonMut.isPending;

  // ---------- リプレイ開閉 ----------
  const openReplay = (src: ReplaySource) => {
    setReplay(src);
    setReplayStep(0);
    setMaxReached(0);
    prevNoticeKeyRef.current = "";
    setAuto(false);
    setLineIdx(0);
    setResultShown(false);
    setPopup(null);
  };
  const closeReplay = () => {
    setReplay(null);
    setAuto(false);
  };
  const openLiveReplay = () => {
    if (log.length === 0) return;
    openReplay({
      key: `live-${status.data?.logRunId ?? 0}`,
      runId: status.data?.logRunId ?? 0,
      title: run ? "進行中の潜行" : "直近の潜行",
      course: run?.course ?? lastResult?.course ?? log.length,
      difficultyLabel: run?.difficultyLabel ?? lastResult?.difficultyLabel ?? "",
      result: hasFinishedLatest && lastResult ? lastResult.result : null,
      gained: hasFinishedLatest && lastResult ? lastResult.gained : [],
      lost: hasFinishedLatest && lastResult ? lastResult.lost : [],
      log,
    });
  };
  const openRecordReplay = (rec: RecordRow) => {
    openReplay({
      key: `record-${rec.runId}`,
      runId: rec.runId,
      title: `潜行記録 ― ${rec.result}`,
      course: rec.course,
      difficultyLabel: rec.difficultyLabel,
      result: rec.result,
      gained: rec.gained,
      lost: rec.lost,
      log: rec.log,
    });
  };
  const stepReplay = (n: number) => {
    if (flashing) return; // 明滅中は移動できない
    setReplayStep(Math.max(0, Math.min(n, totalSteps - 1)));
    setAuto(false);
  };

  // ---------- テキスト送り（クリック／キー共通） ----------
  // 行が残っていれば次の行へ。送り終わっていれば、リプレイでは次の階へ、
  // 終了場面ではリザルト表示へ進む。
  const advanceText = () => {
    if (flashing) return; // 絶頂の明滅中はテキスト送り不可。終わってから送れる
    if (resultShown || backlogOpen) return;
    // ポップアップ表示中はテキストを進めない。まずポップアップを閉じ、終わってからテキストが出る
    if (noticeActive) {
      dismissNotice();
      return;
    }
    if (!lineDone) {
      setLineIdx((i) => Math.min(i + 1, lines.length));
      return;
    }
    if (resultPending) {
      setResultShown(true);
      return;
    }
    if (replay && !atResult) stepReplay(index + 1);
  };
  const clickTextBox = advanceText;

  // キー操作：Enter／Space／→ でテキスト送り（行→階）、← で前の階、Esc でバックログを閉じる
  useEffect(() => {
    const handler = (e: KeyboardEvent) => {
      const t = e.target;
      if (t instanceof HTMLInputElement || t instanceof HTMLTextAreaElement) return;
      if (e.key === "Escape") {
        if (backlogOpen) setBacklogOpen(false);
        return;
      }
      if (popup || backlogOpen || mode === "picker" || inEquipPhase) return;
      if (e.key === "ArrowLeft") {
        if (replay) {
          e.preventDefault();
          stepReplay(index - 1);
        }
        return;
      }
      if (e.key === "ArrowRight") {
        if (replay) {
          e.preventDefault();
          advanceText();
        }
        return;
      }
      if (e.key === "Enter" || e.key === " ") {
        // ボタン等にフォーカスがある時は、その操作を優先する
        if (t instanceof HTMLElement && (t.tagName === "BUTTON" || t.tagName === "A")) return;
        e.preventDefault();
        advanceText();
      }
    };
    window.addEventListener("keydown", handler);
    return () => window.removeEventListener("keydown", handler);
  });

  // バックログを開いたら、最新（一番下）までスクロールしてスクロールバーを最下部に置く
  useEffect(() => {
    if (!backlogOpen) return;
    const id = requestAnimationFrame(() => {
      const el = backlogRef.current;
      if (el) el.scrollTop = el.scrollHeight;
    });
    return () => cancelAnimationFrame(id);
  }, [backlogOpen]);

  const toggleEquip = (id: number) => {
    setSelectedEquip((prev) => {
      if (prev.includes(id)) return prev.filter((x) => x !== id);
      if (prev.length >= 4) return prev;
      return [...prev, id];
    });
  };

  // ---------- ステータス（現在場面時点） ----------
  const climaxLimit = params.data?.climaxLimit ?? 3;
  const statusLike: StatusLike | null =
    mode === "picker"
      ? null
      : curDigest
        ? {
            // 絶頂カウントと発情は、文章中の絶頂の瞬間を通過した時点で切り替える
            climaxes: climaxesShown,
            climaxLimit,
            arousal: arousalShown,
            sensitivity: curDigest.sensitivity,
            clothes: curDigest.clothes,
            canReturn: curDigest.canReturn,
            returnFloor: curDigest.returnFloor,
            pendingCount: curDigest.pendingCount,
            effects: curDigest.effects,
            sub: replay ? "リプレイ時点" : run?.difficultyLabel ?? "",
          }
        : run
          ? {
              climaxes: run.climaxes,
              climaxLimit: run.climaxLimit,
              arousal: run.arousal,
              sensitivity: run.sensitivity,
              clothes: run.clothes,
              canReturn: run.canReturn,
              returnFloor: run.returnFloor,
              pendingCount: run.pendingItems.length,
              effects: run.effects,
              sub: run.difficultyLabel,
            }
          : null;

  // 罠シーンの表示内容：記録の画像スロットに生成済みの絵があれば絵、
  // 無ければ「絵なし」枠（状態つき）。所属はイベント記録、タイミングは [[trap]] が担う
  const currentRunId = replay ? replay.runId : (status.data?.logRunId ?? null);
  const sceneVisual: SceneVisual = (() => {
    if (!inTrapScene || !entry) return null;
    const img = entry.image;
    if (img.status === "ready" && img.imageUrl) {
      return { kind: "image", src: img.imageUrl, alt: `${entry.digest.systemName ?? ""}系統の罠「${entry.digest.trapName ?? ""}」の場面の絵` };
    }
    const entryFloor = entry.floor;
    return {
      kind: "frame",
      status: img.status,
      trapName: entry.digest.trapName,
      systemName: entry.digest.systemName,
      hasPrompt: img.prompt != null,
      canGenerate: currentRunId != null && (img.status === "awaiting" || img.status === "failed"),
      generateBusy: genImgMut.isPending || img.status === "generating",
      onGenerate:
        currentRunId != null
          ? () => genImgMut.mutate({ runId: currentRunId, floor: entryFloor })
          : null,
    };
  })();

  const stageFloor = replay
    ? (curDigest?.floor ?? 0)
    : run
      ? run.floor
      : mode === "result"
        ? (lastResult?.floorReached ?? 0)
        : 0;
  const badgeText = replay
    ? `リプレイ ― ${curDigest?.floor ?? 0}/${replay.course}階層`
    : run
      ? `${run.floor} / ${run.course} 階層`
      : mode === "result"
        ? "潜行終了"
        : "入口 ― リプレイ選択";

  const inEquipPhase = run?.phase === "equip";
  const showDecision =
    mode === "live" && lineDone && !inEquipPhase && !resultShown;
  const transportBtn =
    "border border-[var(--border)] bg-black/55 px-2.5 py-1.5 text-xs text-[var(--text)] disabled:opacity-40 sm:px-3 sm:text-sm";

  return (
    <div className="flex min-h-dvh flex-col bg-[var(--bg)] text-[var(--text)]">
      <SafeAreaTopScrim backgroundColor="var(--bg)" />

      {/* 一番上：タブ（ポップアップ式）＋テスト操作 */}
      <header className="z-30 flex items-center gap-1 border-b border-[var(--border)] bg-[var(--surface)] px-2 py-1.5 sm:px-3">
        {(
          [
            { key: "equip", label: "所持品" },
            { key: "records", label: "記録・リプレイ" },
            { key: "params", label: "パラメータ" },
          ] as const
        ).map((t) => (
          <button
            key={t.key}
            type="button"
            aria-haspopup="dialog"
            aria-expanded={popup === t.key}
            onClick={() => setPopup(t.key)}
            className="font-serif-jp border border-transparent px-3 py-1.5 text-sm text-[var(--dim)] hover:text-[var(--text)] sm:text-base"
          >
            {t.label}
          </button>
        ))}
        <button
          type="button"
          aria-haspopup="dialog"
          aria-expanded={popup === "test"}
          onClick={() => setPopup("test")}
          className="ml-auto border border-[var(--border)] px-3 py-1.5 text-xs text-[var(--dim)]"
        >
          テスト操作
        </button>
      </header>

      {/* リプレイ操作バー（ステージ上部） */}
      {replay && (
        <div className="z-30 border-b border-[var(--border)] bg-[var(--surface2)] px-2 py-1.5 sm:px-3" aria-label="リプレイ操作">
          <div className="mx-auto flex max-w-6xl flex-wrap items-center gap-1.5">
            <span className="font-serif-jp mr-1 hidden text-sm text-[var(--accent)] md:inline">{replay.title}</span>
            <button type="button" className={transportBtn} disabled={index <= 0} onClick={() => stepReplay(0)}>
              最初へ
            </button>
            <button type="button" className={transportBtn} disabled={index <= 0} onClick={() => stepReplay(index - 1)}>
              前の階
            </button>
            <button
              type="button"
              onClick={() => {
                if (auto) setAuto(false);
                else {
                  if (index >= totalSteps - 1 && lineDone) {
                    setReplayStep(0);
                    setLineIdx(0);
                    setResultShown(false);
                  }
                  setAuto(true);
                }
              }}
              aria-label={auto ? "自動再生を一時停止" : "自動再生を開始"}
              className="border border-[var(--accent)] bg-[var(--accent)] px-3.5 py-1.5 text-xs font-semibold text-black sm:text-sm"
            >
              {auto ? "一時停止" : index >= totalSteps - 1 && lineDone ? "最初から再生" : "再生"}
            </button>
            <button type="button" className={transportBtn} disabled={index >= totalSteps - 1} onClick={() => stepReplay(index + 1)}>
              次の階
            </button>
            <button type="button" className={transportBtn} disabled={index >= totalSteps - 1} onClick={() => stepReplay(totalSteps - 1)}>
              {resultData ? "リザルトへ" : "最後へ"}
            </button>
            <input
              type="range"
              aria-label="リプレイの再生位置"
              min={0}
              max={Math.max(0, totalSteps - 1)}
              step={1}
              value={Math.min(index, Math.max(0, totalSteps - 1))}
              onChange={(e) => stepReplay(Number(e.target.value))}
              className="min-w-20 flex-1 accent-[#c9a227]"
            />
            <span className="text-xs text-[var(--dim)]">
              {atResult ? "リザルト" : `${index + 1} / ${scenes.length} 階`}
            </span>
            <span className="flex items-center gap-1" role="group" aria-label="再生速度">
              {[1, 1.5, 2].map((s) => (
                <button
                  key={s}
                  type="button"
                  aria-pressed={replaySpeed === s}
                  aria-label={`再生速度 ${s}倍`}
                  onClick={() => setReplaySpeed(s)}
                  className={`border px-1.5 py-1 text-[11px] ${replaySpeed === s ? "border-[var(--accent)] text-[var(--accent)]" : "border-[var(--border)] text-[var(--dim)]"}`}
                >
                  ×{s}
                </button>
              ))}
            </span>
            <button
              type="button"
              onClick={closeReplay}
              aria-label="リプレイを終了して戻る"
              className="border border-[var(--accent-dim)] px-2.5 py-1.5 text-xs text-[var(--accent)]"
            >
              終了
            </button>
          </div>
        </div>
      )}

      {/* ステージ（全画面） */}
      <main className="relative min-h-0 flex-1">
        <div
          className="gold-frame absolute inset-0 overflow-hidden bg-black"
          onWheel={(e) => {
            // ビジュアルノベル式：ホイールを上に回したらバックログを開く
            if (e.deltaY < 0 && !backlogOpen && mode !== "picker" && !inEquipPhase && scenes.length > 0) {
              setBacklogOpen(true);
            }
          }}
        >
          {/* 3D回廊 ⇄ 画像 のトランジション層。罠シーン中（[[trap]] 通過〜 [[scene-end]]／文章終了）は絵へ遷移する */}
          <SceneStage floor={stageFloor} active={mode !== "picker"} visual={sceneVisual} />

          {/* 階層バッジ（左上・位置不変） */}
          <div className="pointer-events-none absolute left-3 top-3 z-30 max-w-[48vw] truncate border border-[var(--accent-dim)] bg-black/60 px-2.5 py-1">
            <span className="font-serif-jp text-xs tracking-wide sm:text-sm sm:tracking-widest">{badgeText}</span>
          </div>

          {/* ステータス（右上）。中央パネル表示中は重なりを避けて隠す */}
          {statusLike && !resultShown && !inEquipPhase && (
            <div className="pointer-events-none absolute right-3 top-3 z-30">
              <StatusPanel s={statusLike} />
            </div>
          )}

          {/* 中央通知 */}
          <CenterNotice text={notice.text} visible={notice.visible && mode !== "picker"} />

          {/* 絶頂の瞬間の画面明滅（明滅中はテキスト送り不可） */}
          {flashKey > 0 && (
            <div key={flashKey} aria-hidden="true" className="climax-flash pointer-events-none absolute inset-0 z-[35]" />
          )}

          {resultShown && <div className="fade-to-black pointer-events-none absolute inset-0 z-10 bg-black/55" />}

          {/* リザルト（クリックで表示） */}
          {resultShown && resultData && (
            <div className="absolute inset-0 z-30 flex items-center justify-center overflow-y-auto p-3">
              <section aria-label="リザルト" className="gold-frame text-in w-full max-w-md bg-[var(--surface)]/95 p-5">
                <p className="text-xs tracking-widest text-[var(--dim)]">リザルト</p>
                <p className="font-serif-jp mt-1 text-3xl text-[var(--accent)]">{resultData.result}</p>
                <p className="mt-1 text-sm text-[var(--dim)]">
                  {resultData.course}階コース（{resultData.difficultyLabel}）／到達 {resultData.floorReached} 階／絶頂 {resultData.climaxes} 回
                </p>
                {resultData.gained.length > 0 && (
                  <div className="mt-3">
                    <p className="text-xs text-[var(--dim)]">確定したアイテム</p>
                    <ul className="mt-1 space-y-1 text-sm">
                      {resultData.gained.map((g, i) => (
                        <li key={i}><ItemLine item={g} /></li>
                      ))}
                    </ul>
                  </div>
                )}
                {resultData.lost.length > 0 && (
                  <div className="mt-3">
                    <p className="text-xs text-[var(--dim)]">失ったアイテム（仮保持分のみ）</p>
                    <ul className="mt-1 space-y-1 text-sm text-[var(--dim)]">
                      {resultData.lost.map((g, i) => (
                        <li key={i}><ItemLine item={g} /></li>
                      ))}
                    </ul>
                  </div>
                )}
                <div className="mt-4 flex flex-wrap gap-2">
                  {replay ? (
                    <button
                      type="button"
                      onClick={closeReplay}
                      className="border border-[var(--accent-dim)] px-4 py-2 text-sm text-[var(--accent)]"
                    >
                      リプレイを終了する
                    </button>
                  ) : (
                    <>
                      <button
                        type="button"
                        onClick={openLiveReplay}
                        className="border border-[var(--accent-dim)] px-4 py-2 text-sm text-[var(--accent)]"
                      >
                        この潜行をリプレイで見返す
                      </button>
                      <button
                        type="button"
                        onClick={() => {
                          setPickerForced(true);
                          setResultShown(false);
                        }}
                        className="border border-[var(--border)] px-4 py-2 text-sm text-[var(--dim)]"
                      >
                        リプレイを選ぶ画面へ
                      </button>
                    </>
                  )}
                </div>
              </section>
            </div>
          )}

          {/* 装備選択（潜行開始直後の判断） */}
          {inEquipPhase && run && (
            <div className="absolute inset-0 z-30 flex items-center justify-center overflow-y-auto p-3">
              <section aria-label="装備選択" className="gold-frame text-in w-full max-w-md bg-[var(--surface)]/95 p-5">
                <p className="text-xs tracking-widest text-[var(--dim)]">装備選択</p>
                <p className="font-serif-jp mt-1 text-lg">
                  {run.course}階コース（{run.difficultyLabel}）
                </p>
                <p className="mt-1 text-sm text-[var(--dim)]">
                  装備を選んでください（最大4つ）。帰還可能になるのは {run.returnFloor} 階からです。
                </p>
                <div className="mt-3 max-h-[38dvh] overflow-y-auto">
                  <EquipPicker items={inventory.data?.items ?? []} selected={selectedEquip} onToggle={toggleEquip} />
                </div>
                <button
                  type="button"
                  disabled={busy}
                  onClick={() => equipMut.mutate(selectedEquip)}
                  className="mt-4 w-full border border-[var(--accent)] bg-[var(--accent)] px-4 py-2.5 font-semibold text-black disabled:opacity-40"
                >
                  装備を確定して潜行する
                </button>
                <button
                  type="button"
                  disabled={busy}
                  onClick={() => abandonMut.mutate(undefined)}
                  className="mt-2 w-full border border-[var(--border)] px-4 py-2 text-sm text-[var(--dim)]"
                >
                  中断する
                </button>
                {actionError && (
                  <p role="alert" className="mt-2 border border-[var(--danger)] bg-black/30 px-2.5 py-1.5 text-sm text-[#e88]">
                    {actionError}
                  </p>
                )}
              </section>
            </div>
          )}

          {/* リプレイ選択画面（潜行中でない時の初期画面） */}
          {mode === "picker" && (
            <div className="absolute inset-0 z-30 flex items-center justify-center overflow-y-auto p-3">
              <section aria-label="リプレイ選択" className="gold-frame text-in w-full max-w-lg bg-[var(--surface)]/95 p-5">
                <p className="text-xs tracking-widest text-[var(--dim)]">リプレイ選択</p>
                <p className="font-serif-jp mt-1 text-xl">どの潜行を見返しますか</p>
                <p className="mt-1 text-sm text-[var(--dim)]">
                  所持品 {status.data?.inventoryCount ?? 0} 件／記録 {status.data?.recordCount ?? 0} 件
                </p>
                {status.isPending && <p className="mt-3 text-sm text-[var(--dim)]">読み込み中…</p>}
                <div className="mt-3 max-h-[44dvh] space-y-2 overflow-y-auto">
                  {log.length > 0 && (
                    <button
                      type="button"
                      onClick={openLiveReplay}
                      className="flex w-full items-center justify-between gap-2 border border-[var(--accent-dim)] bg-black/30 px-3 py-2.5 text-left"
                    >
                      <span>
                        <span className="font-serif-jp text-[var(--accent)]">直近の潜行</span>
                        <span className="ml-2 text-xs text-[var(--dim)]">
                          {lastResult
                            ? `${lastResult.course}階コース（${lastResult.difficultyLabel}）／${lastResult.result}・到達${lastResult.floorReached}階`
                            : `${log.length}階まで進行`}
                        </span>
                      </span>
                      <span className="shrink-0 text-xs text-[var(--accent)]">リプレイ</span>
                    </button>
                  )}
                  {(records.data?.records ?? []).map((rec) => (
                    <button
                      key={rec.runId}
                      type="button"
                      disabled={rec.log.length === 0}
                      onClick={() => openRecordReplay(rec)}
                      aria-label={`${rec.result}の潜行をリプレイで見返す`}
                      className="flex w-full items-center justify-between gap-2 border border-[var(--border)] bg-black/30 px-3 py-2.5 text-left disabled:opacity-40"
                    >
                      <span>
                        <span className="font-serif-jp text-lg text-[var(--accent)]">{rec.result}</span>
                        <span className="ml-2 text-xs text-[var(--dim)]">
                          {rec.course}階コース（{rec.difficultyLabel}）／到達 {rec.floorReached} 階／絶頂 {rec.climaxes} 回／
                          {new Date(rec.startedAt).toLocaleString("ja-JP")}
                        </span>
                      </span>
                      <span className="shrink-0 text-xs text-[var(--accent)]">リプレイ</span>
                    </button>
                  ))}
                  {status.data && log.length === 0 && (records.data?.records.length ?? 0) === 0 && (
                    <p className="text-sm leading-relaxed text-[var(--dim)]">
                      まだ見返せる潜行がありません。潜行が行われると、ここからリプレイを選べるようになります。
                    </p>
                  )}
                </div>
                <div className="mt-4 border-t border-[var(--border)] pt-3">
                  <p className="text-xs leading-relaxed text-[var(--dim)]">
                    新しい潜行は通常Claudeが開始します。持ち主が単独で試す場合はテスト操作からどうぞ。
                  </p>
                  <button
                    type="button"
                    onClick={() => setPopup("test")}
                    className="mt-2 border border-[var(--border)] px-4 py-2 text-sm text-[var(--dim)]"
                  >
                    テスト操作を開く（潜行開始など）
                  </button>
                </div>
              </section>
            </div>
          )}

          {/* テキストボックス（VN式・画面下部） */}
          {mode !== "picker" && !inEquipPhase && (
            <div
              role="button"
              tabIndex={0}
              aria-label="テキストを進める"
              onClick={clickTextBox}
              className="absolute inset-x-0 bottom-0 z-30 cursor-pointer border-t border-[var(--accent-dim)] bg-black/78 px-4 pb-[max(0.75rem,env(safe-area-inset-bottom))] pt-3 backdrop-blur-[2px] sm:px-6"
            >
              <div className="mx-auto max-w-4xl">
                <div className="flex items-start justify-between gap-3">
                  <div className={`min-w-0 flex-1 ${noticeActive ? "invisible" : ""}`} aria-hidden={noticeActive}>
                    {atResult ? (
                      <p className="text-xs tracking-wide text-[var(--accent)]">リザルト</p>
                    ) : curDigest ? (
                      <DigestCaption
                        d={curDigest}
                        climaxCountShown={
                          entry
                            ? climaxPassed > 0
                              ? climaxesShown
                              : null
                            : curDigest.climaxed
                              ? curDigest.climaxCount
                              : null
                        }
                      />
                    ) : (
                      <p className="text-xs tracking-wide text-[var(--accent)]">潜行開始</p>
                    )}
                  </div>
                  <div className="flex shrink-0 gap-1.5">
                    {scenes.length > 0 && (
                      <button
                        type="button"
                        aria-label="バックログを開く"
                        onClick={(e) => {
                          e.stopPropagation();
                          setBacklogOpen(true);
                        }}
                        className="border border-[var(--border)] px-2.5 py-1 text-xs text-[var(--dim)]"
                      >
                        ログ
                      </button>
                    )}
                    <button
                      type="button"
                      aria-pressed={auto}
                      aria-label="オートテキスト送りの切り替え"
                      onClick={(e) => {
                        e.stopPropagation();
                        setAuto((a) => !a);
                      }}
                      className={`border px-2.5 py-1 text-xs ${auto ? "border-[var(--accent)] text-[var(--accent)]" : "border-[var(--border)] text-[var(--dim)]"}`}
                    >
                      オート送り {auto ? "ON" : "OFF"}
                    </button>
                  </div>
                </div>

                {/* 本文エリア：高さ固定（3行分）。長い一文はエリア内スクロールになり、ボックス全体は動かない */}
                <div
                  ref={lineAreaRef}
                  className={`mt-1.5 h-[5.7em] overflow-y-auto pr-1 [scrollbar-gutter:stable] [scrollbar-width:thin] ${noticeActive ? "invisible" : ""}`}
                  aria-hidden={noticeActive}
                >
                  {resultShown ? (
                    <p className="font-serif-jp leading-[1.9] text-[var(--dim)]">― リザルト表示中 ―</p>
                  ) : resultPending ? (
                    <p className="font-serif-jp text-in leading-[1.9] text-[var(--accent)]">― クリックでリザルトを表示 ―</p>
                  ) : atResult ? (
                    <p className="font-serif-jp leading-[1.9] text-[var(--dim)]">全ての階を読み終えた。</p>
                  ) : lines.length > 0 ? (
                    lineDone ? (
                      // 送り終わったら最後の1文を出し直さず、終了マーカーだけ出す（二重表示の防止）
                      <p className="font-serif-jp leading-[1.9] text-[var(--dim)]">― この階の文章はここまで ―</p>
                    ) : (
                      <p key={lineIdx} className="font-serif-jp text-in whitespace-pre-wrap leading-[1.9]">
                        {displayLine}
                      </p>
                    )
                  ) : entry ? (
                    run?.phase === "await_text" ? (
                      <p className="font-serif-jp leading-[1.9] text-[var(--flame)]">― 彼女の文章を待っている…</p>
                    ) : (
                      <p className="font-serif-jp leading-[1.9] text-[var(--dim)]">（この階の文章はまだ書かれていません）</p>
                    )
                  ) : status.isPending ? (
                    <p className="font-serif-jp leading-[1.9] text-[var(--dim)]">読み込み中…</p>
                  ) : (
                    <p className="font-serif-jp leading-[1.9] text-[var(--dim)]">
                      潜行が始まった。最初の一歩を待っている。
                    </p>
                  )}
                </div>

                <div className="flex h-5 items-center justify-end">
                  {!lineDone && lines.length > 0 && (
                    <span className="text-xs text-[var(--accent)]" aria-hidden="true">
                      ▼ {lineIdx + 1} / {lines.length}
                    </span>
                  )}
                  {lineDone && !resultShown && !resultPending && (
                    <span className="text-xs text-[var(--dim)]" aria-hidden="true">
                      {replay ? (atResult ? "" : "クリック／キーで階送り・ホイール↑でログ") : "ホイール↑でログ"}
                    </span>
                  )}
                </div>

                {/* 判断UI（その階のテキストを送り終えたら出現） */}
                {showDecision && run?.phase === "dive" && (
                  <div className="mt-1 flex flex-wrap gap-2" onClick={(e) => e.stopPropagation()}>
                    <button
                      type="button"
                      disabled={busy}
                      onClick={() => advanceMut.mutate(undefined)}
                      className="border border-[var(--accent)] bg-[var(--accent)] px-5 py-2.5 font-serif-jp text-base font-semibold text-black disabled:opacity-40"
                    >
                      一階進む
                    </button>
                    <button
                      type="button"
                      disabled={busy || !run.canReturn}
                      onClick={() => returnMut.mutate(undefined)}
                      className="border border-[var(--accent-dim)] px-4 py-2.5 text-sm text-[var(--accent)] disabled:opacity-40"
                    >
                      {run.canReturn ? "帰還する（仮保持が確定します）" : `帰還する（${run.returnFloor}階から可能）`}
                    </button>
                    <button
                      type="button"
                      disabled={busy}
                      onClick={() => abandonMut.mutate(undefined)}
                      className="border border-[var(--border)] px-4 py-2.5 text-sm text-[var(--dim)]"
                    >
                      中断する（仮保持は失われます）
                    </button>
                    {actionError && (
                      <p role="alert" className="w-full border border-[var(--danger)] bg-black/30 px-2.5 py-1.5 text-sm text-[#e88]">
                        {actionError}
                      </p>
                    )}
                  </div>
                )}
                {showDecision && run?.phase === "await_text" && (
                  <div className="mt-1" onClick={(e) => e.stopPropagation()}>
                    <label htmlFor="her-text" className="text-xs text-[var(--dim)]">
                      彼女の文章（通常はClaudeが submit_text で提出します。これは持ち主が単独で試すためのテスト入力です）
                    </label>
                    <textarea
                      id="her-text"
                      aria-label="彼女の文章のテスト入力"
                      value={draftText}
                      onChange={(e) => setDraftText(e.target.value)}
                      rows={3}
                      className="font-serif-jp mt-1.5 w-full border border-[var(--border)] bg-[var(--bg)] p-2.5 leading-relaxed text-[var(--text)] outline-none focus:border-[var(--accent-dim)]"
                      placeholder="セリフ＋地の文＋擬音…（改行ごとに行送り。単独行の [[trap]] [[scene-end]] [[climax]] で演出タイミングを指定できます）"
                    />
                    <button
                      type="button"
                      disabled={busy || draftText.trim().length === 0}
                      onClick={() => submitMut.mutate(draftText)}
                      className="mt-2 border border-[var(--accent)] bg-[var(--accent)] px-4 py-2 font-semibold text-black disabled:opacity-40"
                    >
                      文章を提出する
                    </button>
                    {actionError && (
                      <p role="alert" className="mt-2 border border-[var(--danger)] bg-black/30 px-2.5 py-1.5 text-sm text-[#e88]">
                        {actionError}
                      </p>
                    )}
                  </div>
                )}
              </div>
            </div>
          )}

          {/* バックログ（ホイール上回転／ログボタンで開く。最新が下、開いた時は最下部から） */}
          {backlogOpen && (
            <div className="absolute inset-0 z-40 flex flex-col bg-black/90" role="dialog" aria-modal="true" aria-label="バックログ">
              <div className="flex items-center justify-between border-b border-[var(--border)] px-4 py-2">
                <p className="font-serif-jp text-[var(--accent)]">バックログ</p>
                <button
                  type="button"
                  onClick={() => setBacklogOpen(false)}
                  aria-label="バックログを閉じる"
                  className="border border-[var(--border)] px-3 py-1 text-xs text-[var(--dim)]"
                >
                  閉じる
                </button>
              </div>
              <div ref={backlogRef} className="min-h-0 flex-1 overflow-y-auto px-4 py-4">
                {replay && (
                  <p className="mx-auto mb-4 max-w-3xl text-xs text-[var(--dim)]">
                    リプレイで再生済みの {backlogScenes.length} 階まで表示しています。
                  </p>
                )}
                <ol className="mx-auto max-w-3xl space-y-5">
                  {backlogScenes.map((e, i) => (
                    <li key={i}>
                      <DigestView d={e.digest} />
                      {e.text ? (
                        <div className="mt-1.5 border-l-2 border-[var(--accent-dim)] bg-white/5 px-3 py-2">
                          <p className="font-serif-jp whitespace-pre-wrap leading-[1.9]">{e.text}</p>
                        </div>
                      ) : (
                        <p className="mt-1.5 text-sm text-[var(--dim)]">（この階の文章はまだ書かれていません）</p>
                      )}
                      <ImageSlotInfo
                        entry={e}
                        busy={genImgMut.isPending}
                        onGenerate={
                          currentRunId != null
                            ? (floor) => genImgMut.mutate({ runId: currentRunId, floor })
                            : undefined
                        }
                      />
                    </li>
                  ))}
                </ol>
              </div>
            </div>
          )}
        </div>
      </main>

      {/* ポップアップ：所持品 */}
      {popup === "equip" && (
        <Popup title="所持品" onClose={() => setPopup(null)} wide>
          {inEquipPhase && (
            <p className="mb-3 text-sm text-[var(--flame)]">潜行の装備選択中です。ここからも、画面中央のパネルからも選べます。</p>
          )}
          {(inventory.data?.items.length ?? 0) === 0 ? (
            <p className="text-sm text-[var(--dim)]">所持品はまだありません。潜行で宝箱やボスから得たアイテムが、生還／クリアでここに確定します。</p>
          ) : (
            <ul className="grid gap-2 sm:grid-cols-2">
              {(inventory.data?.items ?? []).map((item) => (
                <li key={item.id} className="flex items-center justify-between gap-2 border border-[var(--border)] bg-[var(--bg)] px-3 py-2.5">
                  <ItemLine item={item} />
                  {inEquipPhase && (
                    <button
                      type="button"
                      aria-pressed={selectedEquip.includes(item.id)}
                      aria-label={`${item.name}を装備に${selectedEquip.includes(item.id) ? "外す" : "加える"}`}
                      onClick={() => toggleEquip(item.id)}
                      className={`shrink-0 border px-2.5 py-1 text-xs ${selectedEquip.includes(item.id) ? "border-[var(--accent)] text-[var(--accent)]" : "border-[var(--border)] text-[var(--dim)]"}`}
                    >
                      {selectedEquip.includes(item.id) ? "装備中" : "装備する"}
                    </button>
                  )}
                  {run && !inEquipPhase && run.equipped.some((e) => e.id === item.id) && (
                    <span className="shrink-0 text-xs text-[var(--accent)]">装備中</span>
                  )}
                </li>
              ))}
            </ul>
          )}
        </Popup>
      )}

      {/* ポップアップ：記録・リプレイ */}
      {popup === "records" && (
        <Popup title="記録・リプレイ" onClose={() => setPopup(null)} wide>
          <div className="mb-3 flex flex-wrap items-center gap-x-3 gap-y-2 border border-[var(--border)] bg-[var(--bg)] px-3 py-2.5">
            <p className="min-w-0 flex-1 text-xs leading-relaxed text-[var(--dim)]">
              罠シーンの絵の生成リクエスト（プロンプト）は、文章の提出時に自動で作成され記録に保存されます。過去の記録分は、ここから後付けで作成できます。
            </p>
            <button
              type="button"
              disabled={preparePromptsMut.isPending}
              onClick={() => preparePromptsMut.mutate(undefined)}
              className="shrink-0 border border-[var(--accent-dim)] px-3 py-1.5 text-xs text-[var(--accent)] disabled:opacity-40"
            >
              未作成の生成リクエストをまとめて作成
            </button>
            {preparePromptsMut.data && (
              <p className="w-full text-xs text-[var(--dim)]">
                {preparePromptsMut.data.prepared} 件を作成しました（対象外・作成済み {preparePromptsMut.data.skipped} 件）
              </p>
            )}
          </div>
          {(records.data?.records.length ?? 0) === 0 ? (
            <p className="text-sm text-[var(--dim)]">記録はまだありません。潜行が終了すると、ここに残ります。</p>
          ) : (
            <div className="space-y-3">
              {(records.data?.records ?? []).map((rec) => (
                <article key={rec.runId} className="border border-[var(--border)] bg-[var(--bg)] p-4">
                  <div className="flex flex-wrap items-baseline gap-x-3">
                    <span className="font-serif-jp text-xl text-[var(--accent)]">{rec.result}</span>
                    <span className="text-sm text-[var(--dim)]">
                      {rec.course}階コース（{rec.difficultyLabel}）／到達 {rec.floorReached} 階／絶頂 {rec.climaxes} 回
                    </span>
                    <span className="text-xs text-[var(--dim)]">
                      {new Date(rec.startedAt).toLocaleString("ja-JP")}
                    </span>
                    <button
                      type="button"
                      disabled={rec.log.length === 0}
                      onClick={() => openRecordReplay(rec)}
                      aria-label={`${rec.result}の潜行をリプレイで見返す`}
                      className="ml-auto border border-[var(--accent-dim)] px-3 py-1 text-xs text-[var(--accent)] disabled:opacity-40"
                    >
                      リプレイ
                    </button>
                  </div>
                  {rec.gained.length > 0 && (
                    <p className="mt-1.5 text-sm">確定：{rec.gained.map((g) => g.name).join("、")}</p>
                  )}
                  {rec.lost.length > 0 && (
                    <p className="mt-1 text-sm text-[var(--dim)]">喪失：{rec.lost.map((g) => g.name).join("、")}</p>
                  )}
                  <details className="mt-2">
                    <summary className="cursor-pointer text-sm text-[var(--dim)]">潜行ログ（{recordVisibleLog(rec).length}件）</summary>
                    <ol className="mt-2 space-y-4 border-t border-[var(--border)] pt-3">
                      {recordVisibleLog(rec).map((entry, i) => (
                        <li key={i}>
                          <DigestView d={entry.digest} />
                          {entry.text && (
                            <div className="mt-1.5 border-l-2 border-[var(--accent-dim)] bg-[var(--surface2)] px-3 py-2">
                              <p className="font-serif-jp whitespace-pre-wrap leading-[1.9]">{entry.text}</p>
                            </div>
                          )}
                          <ImageSlotInfo
                            entry={entry}
                            busy={genImgMut.isPending}
                            onGenerate={(floor) => genImgMut.mutate({ runId: rec.runId, floor })}
                          />
                        </li>
                      ))}
                    </ol>
                  </details>
                </article>
              ))}
            </div>
          )}
        </Popup>
      )}

      {/* ポップアップ：パラメータ */}
      {popup === "params" && (
        <Popup title="パラメータ" onClose={() => setPopup(null)} wide>
          {params.isPending && <p className="text-sm text-[var(--dim)]">読み込み中…</p>}
          {params.data && <ParamsView p={params.data} />}
        </Popup>
      )}

      {/* ポップアップ：テスト操作（潜行開始は潜行中と分離） */}
      {popup === "test" && (
        <Popup title="テスト操作（持ち主用）" onClose={() => setPopup(null)}>
          <p className="text-xs leading-relaxed text-[var(--dim)]">
            潜行の開始・進行は通常Claudeがアクション経由で行います。ここは持ち主が単独で試すための操作です。
          </p>
          {actionError && (
            <p role="alert" className="mt-2 border border-[var(--danger)] bg-black/30 px-2.5 py-1.5 text-sm text-[#e88]">
              {actionError}
            </p>
          )}
          {!run && (
            <div className="mt-3 space-y-3">
              <div>
                <p className="text-xs text-[var(--dim)]">コース</p>
                <div className="mt-1 grid grid-cols-3 gap-1.5" role="group" aria-label="コース選択">
                  {COURSES.map((c) => (
                    <button
                      key={c}
                      type="button"
                      aria-pressed={course === c}
                      onClick={() => setCourse(c)}
                      className={`border px-2 py-2 text-sm ${course === c ? "border-[var(--accent)] text-[var(--accent)]" : "border-[var(--border)] text-[var(--dim)]"}`}
                    >
                      {c}階
                    </button>
                  ))}
                </div>
                <p className="mt-1 text-xs text-[var(--dim)]">帰還は {Math.ceil(course / 2)} 階到達後から可能になります</p>
              </div>
              <div>
                <p className="text-xs text-[var(--dim)]">難易度</p>
                <div className="mt-1 grid grid-cols-3 gap-1.5" role="group" aria-label="難易度選択">
                  {DIFFS.map((d) => (
                    <button
                      key={d.key}
                      type="button"
                      aria-pressed={difficulty === d.key}
                      onClick={() => setDifficulty(d.key)}
                      className={`border px-2 py-2 text-sm ${difficulty === d.key ? "border-[var(--accent)] text-[var(--accent)]" : "border-[var(--border)] text-[var(--dim)]"}`}
                    >
                      {d.label}
                    </button>
                  ))}
                </div>
              </div>
              <button
                type="button"
                disabled={busy}
                onClick={() => startMut.mutate(undefined)}
                className="w-full border border-[var(--accent)] bg-[var(--accent)] px-4 py-2.5 font-semibold text-black disabled:opacity-40"
              >
                潜行を開始する
              </button>
            </div>
          )}
          {inEquipPhase && (
            <p className="mt-3 text-sm leading-relaxed text-[var(--dim)]">
              装備選択中です。画面中央のパネル、または「所持品」タブから装備を選んでください。
            </p>
          )}
          {run?.phase === "dive" && (
            <div className="mt-3 space-y-2">
              <button
                type="button"
                disabled={busy}
                onClick={() => advanceMut.mutate(undefined)}
                className="w-full border border-[var(--accent)] bg-[var(--accent)] px-4 py-3 font-serif-jp text-lg font-semibold text-black disabled:opacity-40"
              >
                一階進む
              </button>
              <button
                type="button"
                disabled={busy || !run.canReturn}
                onClick={() => returnMut.mutate(undefined)}
                className="w-full border border-[var(--accent-dim)] px-4 py-2.5 text-[var(--accent)] disabled:opacity-40"
              >
                {run.canReturn ? "帰還する（仮保持が確定します）" : `帰還する（${run.returnFloor}階から可能）`}
              </button>
              <button
                type="button"
                disabled={busy}
                onClick={() => abandonMut.mutate(undefined)}
                className="w-full border border-[var(--border)] px-4 py-2 text-sm text-[var(--dim)]"
              >
                中断する（仮保持は失われます）
              </button>
            </div>
          )}
          {run?.phase === "await_text" && (
            <p className="mt-3 text-sm leading-relaxed text-[var(--dim)]">
              文章待ちです。画面下部のテキストボックス内の入力欄から文章を提出すると、次の階へ進めます。
            </p>
          )}
          {run && (
            <div className="mt-4 border-t border-[var(--border)] pt-3 text-sm text-[var(--dim)]">
              <p>
                進行中：{run.course}階コース（{run.difficultyLabel}）／現在 {run.floor} 階／
                {run.phase === "equip" ? "装備選択中" : run.phase === "await_text" ? "文章待ち" : "潜行中"}
              </p>
              {run.equipped.length > 0 && (
                <ul className="mt-2 space-y-1">
                  {run.equipped.map((g) => (
                    <li key={g.id}><ItemLine item={g} /></li>
                  ))}
                </ul>
              )}
              {run.pendingItems.length > 0 && (
                <div className="mt-2">
                  <p className="text-xs">仮保持アイテム（生還／クリアで確定、敗北で消失）</p>
                  <ul className="mt-1 space-y-1">
                    {run.pendingItems.map((g, i) => (
                      <li key={i}><ItemLine item={g} /></li>
                    ))}
                  </ul>
                </div>
              )}
            </div>
          )}
        </Popup>
      )}
    </div>
  );
}

function EquipPicker({
  items,
  selected,
  onToggle,
}: {
  items: InvItem[];
  selected: number[];
  onToggle: (id: number) => void;
}) {
  if (items.length === 0) {
    return <p className="text-sm text-[var(--dim)]">所持品がありません。装備なしで潜行できます。</p>;
  }
  return (
    <ul className="space-y-1.5">
      {items.map((item) => (
        <li key={item.id}>
          <label className="flex cursor-pointer items-center gap-2 border border-[var(--border)] bg-[var(--bg)] px-2.5 py-2 text-sm">
            <input
              type="checkbox"
              aria-label={`${item.name}を装備する`}
              checked={selected.includes(item.id)}
              onChange={() => onToggle(item.id)}
              className="accent-[#c9a227]"
            />
            <ItemLine item={item} />
          </label>
        </li>
      ))}
    </ul>
  );
}

function ParamsView({ p }: { p: ApiResponse<typeof api, "get_params"> }) {
  const rows: [string, string][] = [
    ["コース", p.courses.map((c) => `${c}階`).join("／")],
    ["1日のチャレンジ回数", p.dailyChallengeLimit == null ? "無制限（テスト用）" : `${p.dailyChallengeLimit}回`],
    ["ボストラップ", `${p.bossEvery}階ごと`],
    ["帰還条件", p.returnRule],
    ["敗北条件", `絶頂 ${p.climaxLimit} 回`],
    ["装備上限", `${p.maxEquip} つ`],
    ["絶頂時の発情", `${p.arousalOnClimax} に戻る`],
    ["静かなターン", `発情 −${p.quietArousalDecay}／感度は1.0へ ${p.sensitivityDecay} ずつ減衰`],
    ["感度レンジ", `×${p.sensitivityMin.toFixed(1)} 〜 ×${p.sensitivityMax.toFixed(1)}`],
    ["回避成功率", `clamp(${p.avoidBase} + 装備ボーナス − 罠の基礎難度 − 階×${p.depthPerFloor} − 難易度係数, ${p.avoidMin}, ${p.avoidMax})`],
    ["出来事の重み", `静か ${p.eventWeights.quiet}／宝箱 ${p.eventWeights.chest}／罠 ${p.eventWeights.trap}`],
    ["アイテム回避ボーナス", `レアリティ×${p.itemBonusPerRarity} + 0〜${p.itemBonusRandMax}`],
    ["レアリティ重み（通常）", p.rarityWeights.map((w, i) => `${i + 1}:${w}`).join(" ")],
    ["レアリティ重み（ボス産）", p.bossRarityWeights.map((w, i) => `${i + 1}:${w}`).join(" ")],
    ["感度上昇効果", `+${p.effectSensitivityDelta} が ${p.effectSensitivityTurns} ターン`],
    ["脱力効果", `回避 ${p.effectWeaknessAvoid} が ${p.effectWeaknessTurns} ターン`],
    ["拘束効果", `次の回避 ${p.effectBindAvoid}（${p.effectBindTurns} ターン）`],
  ];
  return (
    <div className="space-y-5">
      <p className="border border-[var(--accent-dim)] bg-[var(--surface)] px-3 py-2 text-sm">
        この画面の数値はすべて<span className="font-semibold text-[var(--accent)]">暫定</span>です。実測しながら調整します（パラメータはサーバー側の1か所に集約されています）。
      </p>
      <div className="overflow-x-auto">
        <table className="w-full min-w-[520px] border-collapse text-sm">
          <tbody>
            {rows.map(([k, v]) => (
              <tr key={k} className="border-b border-[var(--border)]">
                <th className="w-44 px-2 py-2 text-left align-top font-normal text-[var(--dim)]">{k}</th>
                <td className="px-2 py-2">{v} <span className="text-xs text-[var(--accent)]">暫定</span></td>
              </tr>
            ))}
            {p.difficulties.map((d) => (
              <tr key={d.key} className="border-b border-[var(--border)]">
                <th className="w-44 px-2 py-2 text-left align-top font-normal text-[var(--dim)]">難易度：{d.label}</th>
                <td className="px-2 py-2">
                  罠難度 {d.trapDiffMod > 0 ? "+" : ""}{d.trapDiffMod}／戦利品価値 ×{d.lootValueMult} <span className="text-xs text-[var(--accent)]">暫定</span>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      <div>
        <h3 className="font-serif-jp text-lg">ボストラップの強化の型 <span className="text-xs text-[var(--accent)]">暫定</span></h3>
        <ul className="mt-2 grid gap-2 sm:grid-cols-2">
          {p.bossTypes.map((t) => (
            <li key={t.key} className="border border-[var(--border)] bg-[var(--surface)] px-3 py-2 text-sm">
              <span className="font-serif-jp">{t.label}</span>
              <span className="text-[var(--dim)]"> ― {t.note}</span>
            </li>
          ))}
        </ul>
        <p className="mt-1.5 text-xs text-[var(--dim)]">
          倍率：持続 ×{p.bossSustainedMult}／段階 ×{p.bossStagedMult}／対象の拡大 ×{p.bossExpandMult}（すべて暫定）
        </p>
      </div>
      <div>
        <h3 className="font-serif-jp text-lg">罠の出現表（系統テンプレート） <span className="text-xs text-[var(--accent)]">暫定</span></h3>
        <div className="mt-2 space-y-2">
          {p.systems.map((s) => (
            <article key={s.key} className="border border-[var(--border)] bg-[var(--surface)] px-3 py-2.5">
              <p className="text-sm">
                <span className="font-serif-jp text-[var(--accent)]">{s.key} {s.name}</span>
                <span className="text-[var(--dim)]">
                  {" "}― 基礎難度 {s.diffMin}〜{s.diffMax}／発情増分 {s.arousalMin}〜{s.arousalMax}
                  {s.effectNote ? `／主な効果：${s.effectNote}` : ""}
                </span>
              </p>
              <p className="mt-1 text-sm leading-relaxed text-[var(--dim)]">{s.traps.join("、")}</p>
            </article>
          ))}
        </div>
      </div>
    </div>
  );
}
