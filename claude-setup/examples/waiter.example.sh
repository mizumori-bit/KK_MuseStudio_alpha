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
#   FLAG_FILE     セッションの旗。本体の Muse が開始時に立て、終了時に下ろす
#   WAITER_FROM_ID 前回処理し終えた最後のイベント ID。張り直しのときに渡すと、
#                 張り直しの間に届いた未処理イベントから再開できる（下記）。
#                 値としての 0 も再開位置として扱う（0 より新しいものを全部拾う）。
#                 未指定のときだけ、初回用に現在分を基準線にする
set -uo pipefail

BRIDGE_URL="${BRIDGE_URL:?ブリッジのURLを環境変数で渡してください}"
TOKEN="${TOKEN:?鍵を環境変数で渡してください}"
FLAG_FILE="${FLAG_FILE:-$HOME/musesession.flag}"
POLL_SECONDS=1
MAX_RUN=1800        # 寿命の上限（秒）。達したら再装填の合図を出して終わる
UNREACH_LIMIT=900   # ブリッジに届かない状態がこの秒数続いたらセッション終了とみなす
APP_GONE_LIMIT=3    # アプリ不在がこの回数続いたら終了

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
