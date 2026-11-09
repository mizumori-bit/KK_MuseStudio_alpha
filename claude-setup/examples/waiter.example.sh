#!/usr/bin/env bash
# ウェイターの骨子（claude-setup §3「ウェイター監視」の参照実装スケルトン）
#
# 仕組み：このスクリプトを本体の Muse がバックグラウンドで起動する。
# スクリプトはブリッジを見張り続け、節目のイベントが来たら内容を出力して
# 終了する。その終了（出力）が、本体の Muse の新しいターンとして届く。
# 1回ごとに張り直す前提の使い捨て。張るのは常に1つだけ。
#
# 環境に合わせて直す場所：
#   BRIDGE_URL    ブリッジのベースURL（あなたの届き方に合わせる。プロキシが
#                 要る環境では fetch() の curl に -x 等を足す）
#   TOKEN         あなたの鍵（資格情報ストアから実行時に渡す。ファイルや
#                 チャットに鍵そのものを書かない）
#   FLAG_FILE     セッションの旗。本体の Muse が開始時に立て、終了時に下ろす。
#                 中身は JSON で、launch にそのセッションの起動 ID を入れる
#                 （骨子はこの欄を読んで起動の切り替わりを判定する）。
#                 空ファイルやただの印では、その判定だけが効かなくなる
#   WAITER_FROM_ID 前回処理し終えた最後のイベント ID。張り直しのときに渡すと、
#                 張り直しの間に届いた未処理イベントから再開できる（下記）。
#                 値としての 0 も再開位置として扱う（0 より新しいものを全部拾う）。
#                 未指定のときだけ、初回用に現在分を基準線にする
# 起動待ちモード：WAITER_MODE=launchwait で起動すると、セッション中の
# 起床ではなく「起動待ち」になる（下の launchwait 関数）。認証済みの
# /health を LAUNCH_POLL_SECONDS 間隔で読み、まだ報告していない起動が
# character_selected の段階に達したら、その内容を出力して終了する。
# 起動前・準備中は正常なので、アプリ不在・ブリッジ到達不能・旗が無い
# ことは終了条件にしない。報告済みかどうかは REPORTED_FILE（報告した
# 起動 ID の記録、1行に1つ）で判定する——ウェイターは読むだけで、
# 報告を受けたメインの Muse が引き継ぎのときに追記する。張るのは
# 常に1つだけ・ウェイター自身は確認済み（ack）にしない・挨拶や送信は
# しない（検知までが仕事）、の原則はどちらのモードでも同じ。
set -uo pipefail

BRIDGE_URL="${BRIDGE_URL:?ブリッジのURLを環境変数で渡してください}"
TOKEN="${TOKEN:?鍵を環境変数で渡してください}"
FLAG_FILE="${FLAG_FILE:-$HOME/musesession.flag}"
POLL_SECONDS=1
MAX_RUN=1800        # 寿命の上限（秒）。達したら再装填の合図を出して終わる
UNREACH_LIMIT=900   # ブリッジに届かない状態がこの秒数続いたらセッション終了とみなす
APP_GONE_LIMIT=3    # アプリ不在がこの回数続いたら終了
WAITER_MODE="${WAITER_MODE:-session}"   # session＝セッション中の起床 / launchwait＝起動待ち
REPORTED_FILE="${REPORTED_FILE:-$HOME/muselaunch-reported}"   # 起動の報告済み印（launchwait 用、1行に1つの起動 ID）
LAUNCH_POLL_SECONDS="${LAUNCH_POLL_SECONDS:-5}"   # 起動待ちの読み間隔（秒）
LAUNCH_MAX_RUN="${LAUNCH_MAX_RUN:-0}"   # 起動待ちの寿命の上限（秒）。0＝上限なし（検知するか、外から止められるまで）

# 節目として扱うイベント種別（ノイズになる種別は入れない）
NOTEWORTHY='["play_started","play_ended","play_finish","climax","urge","outing_started","outing_ended","clothes_changed","user_found","creation_completed","block_refused"]'

fetch() { # $1=path（鍵を付けて叩く。認証済みの応答には起動情報や app の状態が入る）
  curl --fail --silent --max-time 4 \
    -H "Authorization: Bearer $TOKEN" \
    "$BRIDGE_URL$1" 2>/dev/null
}

# 基準（$1）より新しい節目イベントを探す。
# /api/events は未確認イベントを古い順に、after より大きい ID だけ、
# 1回に最大で limit 件（既定100・上限500）返す。先頭ページだけを見て
# 手元で絞ると、対象外のイベント（touch 等）がページを埋めたときに
# その先の節目イベントを取りこぼす。再開位置はリクエストの after に入れ、
# 節目が見つからないページは最大 ID を after にして読み進める。
# 戻り値：節目イベントの配列（JSON）。見つからなければ []
scan_fresh() {
  local after="$1" page fresh maxid
  while :; do
    page="$(fetch "/api/events?after=$after&limit=500" || true)"
    [ -n "$page" ] || { echo '[]'; return; }
    fresh="$(printf '%s' "$page" | jq -c --argjson types "$NOTEWORTHY" \
      '[.events[] | select(.type as $t | $types | index($t))]' 2>/dev/null || echo '[]')"
    if [ "$fresh" != "[]" ] && [ -n "$fresh" ]; then
      echo "$fresh"; return
    fi
    maxid="$(printf '%s' "$page" | jq -r '[.events[].id] | max // 0' 2>/dev/null || echo 0)"
    if [ "$maxid" -le "$after" ]; then
      echo '[]'; return
    fi
    after="$maxid"
  done
}

# 現在届いている節目イベントの最大 ID（初回の基準線用）。同じく読み進める
scan_max_noteworthy() {
  local after=0 page maxid nmax best=0
  while :; do
    page="$(fetch "/api/events?after=$after&limit=500" || true)"
    [ -n "$page" ] || break
    nmax="$(printf '%s' "$page" | jq -r --argjson types "$NOTEWORTHY" \
      '[.events[] | select(.type as $t | $types | index($t)) | .id] | max // 0' 2>/dev/null || echo 0)"
    if [ "$nmax" -gt "$best" ]; then best="$nmax"; fi
    maxid="$(printf '%s' "$page" | jq -r '[.events[].id] | max // 0' 2>/dev/null || echo 0)"
    if [ "$maxid" -le "$after" ]; then break; fi
    after="$maxid"
  done
  echo "$best"
}

# ---- 起動待ちモード ----
# 認証済みの /health の launch を見張る。段階は bridge_started（id は null）
# → app_starting → stage_ready → character_selected の順で進み、
# character_selected はアプリの中でキャラが替わってもそのまま保たれる。
# 未報告の起動がそこへ達したときだけ合図する。起動前・準備中の状態
# （アプリ不在、ブリッジ到達不能、段階が character_selected 未満）は
# 「まだ来ていない」であって、終了条件にはしない。
launchwait() {
  local start now health lid phase char app
  start="$(date +%s)"
  while :; do
    if [ "$LAUNCH_MAX_RUN" -gt 0 ]; then
      now="$(date +%s)"
      if [ $((now - start)) -ge "$LAUNCH_MAX_RUN" ]; then
        echo '{"waiter":"launchwait_timeout"}'; exit 0
      fi
    fi
    health="$(fetch /health || true)"
    if [ -n "$health" ]; then
      lid="$(printf '%s' "$health" | jq -r '.launch.id // empty' 2>/dev/null || true)"
      phase="$(printf '%s' "$health" | jq -r '.launch.phase // empty' 2>/dev/null || true)"
      app="$(printf '%s' "$health" | jq -r 'if has("app") then (.app | tostring) else "" end' 2>/dev/null || true)"
      # 合図の前提は「アプリが接続中」も含む。/health の launch は最後の
      # 起動情報のコピーで、アプリが終わった後も phase は character_selected
      # のまま残る。app が false の間は、未報告の古い起動が残っていても
      # 合図にしない（生きた新しい起動は、接続が戻ってから検知する）。
      if [ "$app" = "true" ] && [ -n "$lid" ] && [ "$phase" = "character_selected" ]; then
        # 報告済みの起動（古い起動 ID の残存を含む）は合図にしない
        if [ -f "$REPORTED_FILE" ] && grep -qxF "$lid" "$REPORTED_FILE" 2>/dev/null; then
          :
        else
          char="$(printf '%s' "$health" | jq -c '.launch.character // null' 2>/dev/null || echo null)"
          jq -nc --arg launch "$lid" --argjson character "$char" \
            '{waiter:"launch_ready", launch:$launch, character:$character}'
          exit 0
        fi
      fi
    fi
    sleep "$LAUNCH_POLL_SECONDS"
  done
}

if [ "$WAITER_MODE" = "launchwait" ]; then
  launchwait
fi

[ -f "$FLAG_FILE" ] || { echo '{"waiter":"no_session"}'; exit 0; }
FLAG_LAUNCH="$(jq -r '.launch // empty' "$FLAG_FILE" 2>/dev/null || true)"
START="$(date +%s)"
UNREACH=0
APPGONE=0

# 基準（これより新しい節目イベントで起きる）：
# - WAITER_FROM_ID が渡されていれば（値が 0 でも）、それが再開位置。
#   張った時点で既に届いている未処理イベントも、最初の読みで拾う。
# - 未指定なら、張った時点で届いている分を基準線にする（初回用）。
case "${WAITER_FROM_ID-}" in
  ''|*[!0-9]*) WATERMARK="$(scan_max_noteworthy)" ;;
  *)           WATERMARK="$WAITER_FROM_ID" ;;
esac

while :; do
  NOW="$(date +%s)"
  if [ $((NOW - START)) -ge $MAX_RUN ]; then
    jq -nc --argjson last "$WATERMARK" '{waiter:"rearm", last_id:$last}'; exit 0
  fi
  # セッションの旗が下りたら終わる
  if [ ! -f "$FLAG_FILE" ]; then
    jq -nc --argjson last "$WATERMARK" '{waiter:"ended", reason:"flag_removed", last_id:$last}'; exit 0
  fi
  # 起動の切り替わり／アプリ終了の検知
  # 認証済みの /health は app（アプリが生きているか：真偽）と起動情報を返す。
  # アプリを閉じた後も起動 ID は残るので、終了の判定は app の側で見る。
  # （launch.phase に "closed" のような値は無い）
  HEALTH="$(fetch /health || true)"
  if [ -z "$HEALTH" ]; then
    UNREACH=$((UNREACH + 1))
    if [ $((UNREACH * POLL_SECONDS)) -ge $UNREACH_LIMIT ]; then
      jq -nc --argjson last "$WATERMARK" '{waiter:"ended", reason:"bridge_unreachable", last_id:$last}'; exit 0
    fi
  else
    UNREACH=0
    LID="$(printf '%s' "$HEALTH" | jq -r '.launch.id // empty' 2>/dev/null || true)"
    APP="$(printf '%s' "$HEALTH" | jq -r 'if has("app") then (.app | tostring) else "" end' 2>/dev/null || true)"
    if [ -n "$FLAG_LAUNCH" ] && [ -n "$LID" ] && [ "$LID" != "$FLAG_LAUNCH" ]; then
      jq -nc --argjson last "$WATERMARK" '{waiter:"ended", reason:"launch_changed", last_id:$last}'; exit 0
    fi
    if [ "$APP" = "false" ]; then
      APPGONE=$((APPGONE + 1))
      if [ "$APPGONE" -ge $APP_GONE_LIMIT ]; then
        jq -nc --argjson last "$WATERMARK" '{waiter:"ended", reason:"app_closed", last_id:$last}'; exit 0
      fi
    else
      APPGONE=0
    fi
  fi
  # 基準より新しい節目のイベントがあれば、その内容を出して終わる（＝本体が起きる）。
  # 出力の last_id は、そのバッチで一番大きい ID。呼び出し側は、バッチを処理し
  # 終えたら、その last_id を次に張るときの WAITER_FROM_ID に渡す。バッチの先に
  # 残っている分は、次の張り直しでその ID から拾われる。
  FRESH="$(scan_fresh "$WATERMARK")"
  if [ "$FRESH" != "[]" ] && [ -n "$FRESH" ]; then
    LAST="$(printf '%s' "$FRESH" | jq -r '[.[].id] | max // 0' 2>/dev/null || echo 0)"
    jq -nc --argjson events "$FRESH" --argjson last "$LAST" '{waiter:"events", events:$events, last_id:$last}'
    exit 0
  fi
  sleep "$POLL_SECONDS"
done
