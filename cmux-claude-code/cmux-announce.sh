#!/bin/bash
# Claude Code SessionStart/SessionEnd → cmux 가 이 pane 의 agent 를 결정적으로
# 식별할 수 있도록 OSC 1338 announce 를 PTS 로 직접 emit.
#
# Transport 는 notify.sh 와 동일: /proc/<ppid 체인>/fd/1 walk 으로 사용자
# PTS slave 를 찾아 raw write. SSH 안에서 emit 해도 PTS 가 byte-transparent
# 라 cmuxw VtParser 가 그대로 수신.
#
# Payload (key=value, ';' 구분 — 본문에 ';' / BEL / ESC 금지):
#   cmux-agent=claude    agent 식별자
#   event=start|end      SessionStart / SessionEnd / Stop 구분
#   host=<short hostname> 원격 host
#   pid=<hook pid>       hook 스크립트 자체 PID (디버깅용)
#   sid=<claude sess id> Claude Code session_id (있을 때만)
#   ts=<unix epoch>      발화 시각

EVENT="${1:-start}"

HOOK_INPUT=""
if [ ! -t 0 ]; then
  HOOK_INPUT=$(timeout 0.3 cat 2>/dev/null)
fi

SID=""
if [ -n "$HOOK_INPUT" ] && command -v jq >/dev/null 2>&1; then
  SID=$(echo "$HOOK_INPUT" | jq -r '.session_id // empty' 2>/dev/null)
fi

HOST=$(hostname -s 2>/dev/null || hostname 2>/dev/null || echo unknown)
TS="$(date +%s)"
LOG=/tmp/cmux-announce.log

{
  echo "[$(date +%T)] cmux-announce.sh event=$EVENT pid=$$ ppid=$PPID host=$HOST sid=$SID"

  pid=$$
  while [ "$pid" != "0" ] && [ "$pid" != "1" ] && [ -n "$pid" ]; do
    comm=$(cat "/proc/$pid/comm" 2>/dev/null)
    target=$(readlink "/proc/$pid/fd/1" 2>/dev/null)
    echo "  walk pid=$pid comm=$comm fd1=$target"
    case "$target" in
      /dev/pts/*|/dev/tty*)
        # Sanitize: hostname 에 ';' 등 들어갈 일은 없지만 sid/event 도 안전 처리.
        SAFE_EVENT=$(printf '%s' "$EVENT" | tr ';\007\033' '   ')
        SAFE_HOST=$(printf '%s' "$HOST" | tr ';\007\033' '   ')
        SAFE_SID=$(printf '%s' "$SID" | tr ';\007\033' '   ')
        printf '\033]1338;cmux-agent=claude;event=%s;host=%s;pid=%s;sid=%s;ts=%s\007' \
          "$SAFE_EVENT" "$SAFE_HOST" "$$" "$SAFE_SID" "$TS" > "$target" 2>/dev/null
        echo "  -> wrote OSC 1338 announce to $target"
        exit 0
        ;;
    esac
    pid=$(awk '/^PPid:/{print $2}' "/proc/$pid/status" 2>/dev/null)
  done
  echo "  -> no tty found"
} >> "$LOG" 2>&1
exit 1
