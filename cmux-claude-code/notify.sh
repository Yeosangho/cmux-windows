#!/bin/bash
msg="${1:-Claude Code 응답 완료}"

# Read hook input JSON from stdin (with timeout to avoid blocking on manual invocation)
HOOK_INPUT=""
if [ ! -t 0 ]; then
  HOOK_INPUT=$(timeout 0.3 cat 2>/dev/null)
fi

# Extract a short snippet of claude's most recent assistant text from the transcript
SUMMARY=""
if [ -n "$HOOK_INPUT" ] && command -v jq >/dev/null 2>&1; then
  TRANSCRIPT=$(echo "$HOOK_INPUT" | jq -r '.transcript_path // empty' 2>/dev/null)
  if [ -z "$TRANSCRIPT" ]; then
    SESSION_ID=$(echo "$HOOK_INPUT" | jq -r '.session_id // empty' 2>/dev/null)
    PROJECT_DIR=$(echo "$PWD" | sed 's|/|-|g')
    for candidate in \
      "/root/.claude/projects/$PROJECT_DIR/$SESSION_ID.jsonl" \
      "/root/.claude/projects/$PROJECT_DIR/sessions/$SESSION_ID.jsonl"; do
      [ -f "$candidate" ] && TRANSCRIPT="$candidate" && break
    done
  fi
  if [ -f "$TRANSCRIPT" ]; then
    sleep 0.3
    SUMMARY=$(tac "$TRANSCRIPT" 2>/dev/null \
      | jq -rR 'try fromjson catch empty | select(.type == "assistant") | .message.content[]? | select(.type == "text") | .text' 2>/dev/null \
      | head -1 \
      | tr '\n;\007\033' '    ' \
      | awk '{print substr($0, 1, 100)}')
  fi
fi

DIR=$(basename "$PWD")
if [ -n "$SUMMARY" ]; then
  BODY="$DIR — $SUMMARY"
else
  BODY="$DIR"
fi

{
  echo "[$(date +%T)] notify.sh fired msg='$msg' pid=$$ ppid=$PPID"
  echo "  transcript=$TRANSCRIPT"
  echo "  summary='$SUMMARY'"
  echo "  summary bytes (xxd):"
  printf '%s' "$SUMMARY" | xxd | sed 's/^/    /'
  echo "  body='$BODY'"
  echo "  body bytes (xxd):"
  printf '%s' "$BODY" | xxd | sed 's/^/    /'
  echo "  full OSC payload bytes (xxd):"
  printf '\033]99;t=Claude Code %s;b=%s;i=test;ts=test\007' "$msg" "$BODY" | xxd | sed 's/^/    /'
  pid=$$
  while [ "$pid" != "0" ] && [ "$pid" != "1" ] && [ -n "$pid" ]; do
    comm=$(cat "/proc/$pid/comm" 2>/dev/null)
    target=$(readlink "/proc/$pid/fd/1" 2>/dev/null)
    echo "  walk pid=$pid comm=$comm fd1=$target"
    case "$target" in
      /dev/pts/*|/dev/tty*)
        ID="$(date +%s)-$$"
        TS="$(date +%s)"
        printf '\033]99;t=Claude Code %s;b=%s;i=%s;ts=%s\007' "$msg" "$BODY" "$ID" "$TS" > "$target" 2>/dev/null
        echo "  -> wrote to $target body='$BODY'"
        exit 0
        ;;
    esac
    pid=$(awk '/^PPid:/{print $2}' "/proc/$pid/status" 2>/dev/null)
  done
  echo "  -> no tty found"
} >> /tmp/claude-notify.log 2>&1
exit 1
