# Claude Code 훅 정리

cmux 가 Claude Code 세션 lifecycle 을 추적하기 위해 Claude Code 측에 설정한 두 종류의 hook:

1. **알림 hook** (`notify.sh` / OSC 99) — 응답 종료 / 질문 대기 / 입력 요청 이벤트를 cmuxw 토스트로 띄움
2. **agent announce hook** (`cmux-announce.sh` / OSC 1338) — SessionStart / SessionEnd 에서 "이 pane 안에 Claude 가 살아있다" 라는 결정적 신호를 cmuxw 로 보냄. cmuxw 의 broadcast scope (`ClaudeAll` / `ClaudeSsh`) 분류, pane 상태 추적, 재시작 후 `claude --resume` 복원이 모두 이 신호에 의존.

## 구성

| 파일 | 역할 |
|---|---|
| `~/.claude/settings.json` (해당 부분) | 어떤 이벤트에서 어떤 hook 스크립트를 부를지 정의 |
| `~/.claude/notify.sh` | 응답 종료 시 transcript 에서 마지막 assistant 텍스트 추출 → OSC 99 페이로드를 사용자 PTS 로 송신 |
| `~/.claude/cmux-announce.sh` | SessionStart / SessionEnd 시 OSC 1338 (`cmux-agent=claude;event=start\|end;host=...;sid=...;ts=...`) 페이로드를 PTS 로 송신 |
| `/tmp/claude-notify.log` | notify.sh 진단 로그 (fire 시간, transcript, summary, OSC payload xxd dump) |
| `/tmp/cmux-announce.log` | cmux-announce.sh 진단 로그 (fire 시간, event, host, sid, PTS walk) |

## 동작 흐름

```
Claude Code 이벤트 발생
        │
        ▼
settings.json hook 매칭
        │
        ├─ Stop          → notify.sh "응답 완료"
        ├─ PreToolUse    → notify.sh "질문 대기"   (matcher: AskUserQuestion|ExitPlanMode)
        ├─ Elicitation   → notify.sh "입력 요청"
        ├─ SessionStart  → cmux-announce.sh "start"
        └─ SessionEnd    → cmux-announce.sh "end"
                │
                ▼
        notify.sh / cmux-announce.sh 실행
                │
                ├─ stdin JSON 읽기 (transcript_path / session_id)
                ├─ (notify.sh) transcript jsonl 후방 검색 → assistant.text 추출 + sanitize
                ├─ (cmux-announce.sh) hostname + session_id + ts 구성
                ├─ /proc/<ppid 체인>/fd/1 walk 으로 사용자 PTS 탐색
                └─ printf '\033]<code>;<payload>\007' → /dev/pts/N
                        │              │
                        │              └─ notify.sh : OSC 99  (t=...;b=...;i=...;ts=...)
                        │                cmux-announce.sh : OSC 1338
                        │                  (cmux-agent=claude;event=start|end;host=...;pid=...;sid=...;ts=...)
                        ▼
                cmux PTY 수신
                        │
                        ├─ OSC 99   → NotificationService.AddNotification → toast
                        └─ OSC 1338 → TerminalSession.AnnouncedAgent set/clear
                                       → broadcast scope (ClaudeAll/ClaudeSsh) 자동 분류
                                       → PaneStateSnapshot.ClaudeRunningInside flag
                                       → AutoRestoreCommand 복원 흐름의 1차 ground truth
```

OSC 99 (알림) 와 OSC 1338 (announce) 는 같은 transport (PTS) 를 공유합니다. SSH 안에서 emit 한 경우 양쪽 모두 byte-transparent 로 cmuxw 까지 도달.

## settings.json (관련 부분)

`~/.claude/settings.json`의 `hooks` 블록만 발췌:

```json
{
  "hooks": {
    "PreToolUse": [
      {
        "matcher": "AskUserQuestion|ExitPlanMode",
        "hooks": [
          { "type": "command", "command": "/root/.claude/notify.sh \"질문 대기\"" }
        ]
      }
    ],
    "Stop": [
      {
        "matcher": "",
        "hooks": [
          { "type": "command", "command": "/root/.claude/notify.sh \"응답 완료\"" }
        ]
      }
    ],
    "Elicitation": [
      {
        "matcher": "",
        "hooks": [
          { "type": "command", "command": "/root/.claude/notify.sh \"입력 요청\"" }
        ]
      }
    ],
    "SessionStart": [
      {
        "matcher": "",
        "hooks": [
          { "type": "command", "command": "/root/.claude/cmux-announce.sh start" }
        ]
      }
    ],
    "SessionEnd": [
      {
        "matcher": "",
        "hooks": [
          { "type": "command", "command": "/root/.claude/cmux-announce.sh end" }
        ]
      }
    ]
  }
}
```

### 등록 이벤트와 발화 시점

| 이벤트 | matcher | 발화 시점 | 즉시성 | 호출 hook |
|---|---|---|---|---|
| `PreToolUse` | `AskUserQuestion\|ExitPlanMode` | claude 가 사용자에게 질문/플랜 승인을 요청하는 도구를 부르기 직전 | 즉시 | `notify.sh "질문 대기"` |
| `Stop` | `""` (모든 정지) | claude 가 응답을 마치는 시점 | 즉시 | `notify.sh "응답 완료"` |
| `Elicitation` | `""` | MCP 서버가 elicitation 으로 사용자 입력을 요청 | 즉시 | `notify.sh "입력 요청"` |
| `SessionStart` | `""` | claude 세션이 시작될 때 (한 번) | 즉시 | `cmux-announce.sh start` |
| `SessionEnd` | `""` | claude 세션이 정상 종료될 때 (한 번) | 즉시 | `cmux-announce.sh end` |

> 의도적으로 빠진 이벤트:
> - **`Notification`** (= "입력 대기"): 약 60초 idle 후 발화 → 노이즈 많아 제외.
> - **`PermissionRequest`**: 한국어 메시지가 다른 hook과 동일 시점에 함께 떠 중복으로 인식되어 제외.

## notify.sh

`~/.claude/notify.sh` (실행 권한 필수: `chmod +x`):

```bash
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
```

### 주요 설계 결정 이유

| 항목 | 결정 | 이유 |
|---|---|---|
| 출력 대상 | `/proc/<ppid 체인>/fd/1` walk로 PTS 탐색 후 직접 write | 훅 서브프로세스에는 controlling TTY가 없어 `/dev/tty` open이 ENXIO. 사용자 claude의 stdout fd가 가리키는 PTS slave가 가장 신뢰할 만한 출력처. |
| Transcript 위치 | hook stdin JSON의 `transcript_path` 우선, 없으면 `session_id`+cwd로 후보 경로 시도 | 공식 입력에서 직접 받는 게 가장 정확. fallback은 호환용. |
| Summary 추출 | `tac` + `jq -rR 'try fromjson catch empty'` + 마지막 `assistant.text` | 가장 최근 응답 1건만 필요. `try fromjson catch empty`로 중간에 jsonl이 깨진 라인 무시. |
| Sleep 0.3s | transcript 읽기 직전 | Stop 이벤트가 transcript 마지막 라인 flush보다 빠르게 발화해 한 턴 이전 응답이 잡히는 race 회피. |
| Truncate | `awk '{print substr($0, 1, 100)}'` (gawk 5.x) | UTF-8 char-aware. `cut -c`/Python `s[:N]` 모두 검증했지만 awk substr가 의존성 가장 적음. |
| Sanitize | `tr '\n;\007\033' '    '` | OSC 페이로드에서 `;`는 필드 구분자, `\007`는 종료자, `\033`는 시퀀스 시작자. 본문에 들어가면 OSC 조기 종료. |
| Title 통일 | `Claude Code <msg>` (예: `Claude Code 응답 완료`) | 제목으로 그룹화하면서도 이벤트별로 구분. |
| ID/TS | `$(date +%s)-$$` / `$(date +%s)` | 매 호출마다 ID가 달라 알림 시스템이 별개의 알림으로 처리. |

## OSC 99 페이로드 형식 (notify.sh)

```
\033]99;t=<TITLE>;b=<BODY>;i=<ID>;ts=<TS>\007
```

| 키 | 값 | 예시 |
|---|---|---|
| `t` | Title | `Claude Code 응답 완료` |
| `b` | Body | `ysh — 완료. 변경 사항 요약: ...` |
| `i` | unique id | `1746543738-3741092` |
| `ts` | unix epoch | `1746543738` |

종료자: BEL(`\007`). 일부 멀티플렉서에서는 ST(`\033\\`)가 더 안정적이라는 보고가 있어 필요 시 교체 가능.

## cmux-announce.sh

`~/.claude/cmux-announce.sh` (실행 권한 필수: `chmod +x`):

```bash
#!/bin/bash
# Claude Code SessionStart/SessionEnd → cmux 가 이 pane 의 agent 를
# 결정적으로 식별할 수 있도록 OSC 1338 announce 를 PTS 로 직접 emit.

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

{
  pid=$$
  while [ "$pid" != "0" ] && [ "$pid" != "1" ] && [ -n "$pid" ]; do
    target=$(readlink "/proc/$pid/fd/1" 2>/dev/null)
    case "$target" in
      /dev/pts/*|/dev/tty*)
        SAFE_EVENT=$(printf '%s' "$EVENT" | tr ';\007\033' '   ')
        SAFE_HOST=$(printf '%s'  "$HOST"  | tr ';\007\033' '   ')
        SAFE_SID=$(printf '%s'   "$SID"   | tr ';\007\033' '   ')
        printf '\033]1338;cmux-agent=claude;event=%s;host=%s;pid=%s;sid=%s;ts=%s\007' \
          "$SAFE_EVENT" "$SAFE_HOST" "$$" "$SAFE_SID" "$TS" > "$target" 2>/dev/null
        exit 0
        ;;
    esac
    pid=$(awk '/^PPid:/{print $2}' "/proc/$pid/status" 2>/dev/null)
  done
} >> /tmp/cmux-announce.log 2>&1
exit 1
```

## OSC 1338 페이로드 형식 (cmux-announce.sh)

```
\033]1338;cmux-agent=<agent>;event=<event>;host=<host>;pid=<pid>;sid=<sid>;ts=<ts>\007
```

| 키 | 값 | 예시 |
|---|---|---|
| `cmux-agent` | agent 식별자 | `claude` |
| `event` | lifecycle event | `start`, `end`, `heartbeat` |
| `host` | hostname (short) — 원격에서 emit 됐을 때 host 식별 | `pnode16` |
| `pid` | hook 스크립트 자체 PID (디버깅용) | `127581` |
| `sid` | Claude Code session_id (stdin JSON 에서 추출) | `1939e3c0-c8ec-481d-aa00-cba26df8aa5a` |
| `ts` | unix epoch (emit 시각) | `1746543738` |

cmuxw 측 수신 흐름:

1. `Cmux.Core.Terminal.OscHandler.HandleOsc1338` 가 key=value 파싱
2. `TerminalSession.AgentAnnounceReceived` 이벤트 발화 + `AnnouncedAgent` / `RemoteHost` / `AnnouncedAt` 상태 업데이트
3. `event=end` 면 `AnnouncedAgent` 클리어
4. daemon-backed pane 의 경우 `DaemonSessionManager` → `DaemonPipeServer` → `DaemonClient.AgentAnnounced` 이벤트로 cmuxw 본체에도 broadcast
5. `BroadcastInputViewModel.InvalidatePane` 가 분류 캐시 무효화 → 다음 broadcast scope 매칭에 즉시 반영
6. `AgentDetector.ClassifyPane` 가 다음 우선순위로 결정: announce 우선 → process tree (claude.exe / ssh.exe cmdline) → buffer-text 휴리스틱 (low-confidence fallback)

종료자도 BEL(`\007`). OSC 99 와 동일 lex 경로 (VtParser 의 OSC state machine) 를 통과하므로 SSH PTY stream 으로 운반 시 byte-transparent.

## 진단 / 디버깅

매 호출마다 `/tmp/claude-notify.log`에 다음이 추가됩니다:

- 발화 시각, msg, pid/ppid
- transcript 경로
- summary 문자열 + xxd dump
- body 문자열 + xxd dump
- 실제로 PTS에 송신될 OSC 페이로드 전체의 xxd dump
- proc tree walk 경로 및 최종 PTS

byte 레벨 검증으로 hook 측 문제와 다운스트림(cmux/PTY) 문제를 분리할 수 있음.

운영 단계에서 로깅이 부담되면 `notify.sh`에서 `echo "  ..." | xxd | sed 's/^/    /'` 라인들을 주석 처리하면 됩니다.

## 알려진 이슈 / 주의사항

1. **여러 claude 세션이 동시에 살아 있을 때**: 각 세션은 시작 시점에 settings.json을 캐시. 이후 settings.json을 수정해도 활성 세션은 영향을 받지 않음. 새 세션이거나 `/hooks` 재로드 후 적용됨.
2. **Stop 훅의 race**: 응답 마지막 텍스트가 transcript에 flush되기 전에 Stop이 발화하면 직전 응답이 추출됨. 현재 `sleep 0.3` 으로 회피 중.
3. **OSC 페이로드 길이 제한**: 본 환경(cmux 측) 페이로드 byte 한계 이슈가 있어 본문이 잘리는 현상이 있었음. cmux 쪽에서 별도 수정 필요.
