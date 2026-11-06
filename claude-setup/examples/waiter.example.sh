#!/usr/bin/env bash
# ウェイターの骨子（claude-setup §3「ウェイター監視」の参照実装スケルトン）
#
# 仕組み：このスクリプトを本体の Muse がバックグラウンドで起動する。
# スクリプトはブリッジを見張り続け、節目のイベントが来たら内容を出力して
# 終了する。その終了（出力）が、本体の Muse の新しいターンとして届く。
# 1回ごとに張り直す前提の使い捨て。張るのは常に1つだけ。
#
# 環境に合わせて直す場所：
#   BRIDGE_URL  ブリッジのベースURL（あなたの届き方に合わせる。プロキシが
#               要る環境では fetch() の curl に -x 等を足す）
#   TOKEN       あなたの鍵（資格情報ストアから実行時に渡す。ファイルや
#               チャットに鍵そのものを書かない）
#   FLAG_FILE   セッションの旗。本体の Muse が開始時に立て、終了時に下ろす
set -uo pipefail

BRIDGE_URL="${BRIDGE_URL:?ブリッジのURLを環境変数で渡してください}"
TOKEN="${TOKEN:?鍵を環境変数で渡してください}"
FLAG_FILE="${FLAG_FILE:-$HOME/musesession.flag}"
POLL_SECONDS=1
MAX_RUN=1800        # 寿命の上限（秒）。達したら再装填の合図を出して終わる
UNREACH_LIMIT=900   # ブリッジに届かない状態がこの秒数続いたらセッション終了とみなす
APP_GONE_LIMIT=3    # /health でアプリ不在がこの回数続いたら終了

# 節目として扱うイベント種別（ノイズになる種別は入れない）
NOTEWORTHY='["play_started","play_ended","play_finish","climax","urge","outing_started","outing_ended","clothes_changed","user_found","creation_completed","block_refused"]'

fetch() { # $1=path
  curl --fail --silent --max-time 4 \
    -H "Authorization: Bearer $TOKEN" \
    "$BRIDGE_URL$1" 2>/dev/null
}

[ -f "$FLAG_FILE" ] || { echo '{"waiter":"no_session"}'; exit 0; }
FLAG_LAUNCH="$(jq -r '.launch // empty' "$FLAG_FILE" 2>/dev/null || true)"
START="$(date +%s)"
UNREACH=0
APPGONE=0

# 張った時点で既にある節目イベントを基準線にする（これらでは起きない）
BASELINE_IDS='[]'
EVENTS_JSON="$(fetch /api/events || true)"
if [ -n "$EVENTS_JSON" ]; then
  BASELINE_IDS="$(printf '%s' "$EVENTS_JSON" | jq -c --argjson types "$NOTEWORTHY" \
    '[.events[] | select(.type as $t | $types | index($t)) | .id]' 2>/dev/null || echo '[]')"
fi

while :; do
  NOW="$(date +%s)"
  if [ $((NOW - START)) -ge $MAX_RUN ]; then
    echo '{"waiter":"rearm"}'; exit 0
  fi
  # セッションの旗が下りたら終わる
  if [ ! -f "$FLAG_FILE" ]; then
    echo '{"waiter":"ended","reason":"flag_removed"}'; exit 0
  fi
  # 起動の切り替わり／アプリ終了の検知
  HEALTH="$(fetch /health || true)"
  if [ -z "$HEALTH" ]; then
    UNREACH=$((UNREACH + 1))
    if [ $((UNREACH * POLL_SECONDS)) -ge $UNREACH_LIMIT ]; then
      echo '{"waiter":"ended","reason":"bridge_unreachable"}'; exit 0
    fi
  else
    UNREACH=0
    LID="$(printf '%s' "$HEALTH" | jq -r '.launch.id // empty' 2>/dev/null || true)"
    PHASE="$(printf '%s' "$HEALTH" | jq -r '.launch.phase // empty' 2>/dev/null || true)"
    if [ -n "$FLAG_LAUNCH" ] && [ -n "$LID" ] && [ "$LID" != "$FLAG_LAUNCH" ]; then
      echo '{"waiter":"ended","reason":"launch_changed"}'; exit 0
    fi
    if [ -z "$LID" ] || [ "$PHASE" = "closed" ]; then
      APPGONE=$((APPGONE + 1))
      if [ "$APPGONE" -ge $APP_GONE_LIMIT ]; then
        echo '{"waiter":"ended","reason":"app_closed"}'; exit 0
      fi
    else
      APPGONE=0
    fi
  fi
  # 節目のイベントが増えたら、その内容を出して終わる（＝本体が起きる）
  EVENTS_JSON="$(fetch /api/events || true)"
  if [ -n "$EVENTS_JSON" ]; then
    NEW="$(printf '%s' "$EVENTS_JSON" | jq -c --argjson types "$NOTEWORTHY" --argjson base "$BASELINE_IDS" \
      '[.events[] | select(.type as $t | $types | index($t)) | select(.id as $i | $base | index($i) | not)]' \
      2>/dev/null || echo '[]')"
    if [ "$NEW" != "[]" ] && [ -n "$NEW" ]; then
      printf '%s' "$NEW" | jq -c '{waiter:"events", events: .}'
      exit 0
    fi
  fi
  sleep "$POLL_SECONDS"
done
