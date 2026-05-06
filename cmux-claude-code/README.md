# Claude Code 알림 훅 정리

cmux 토스트 알림을 띄우기 위해 Claude Code 측에 설정한 hook과 호출 스크립트 모음.

## 구성

| 파일 | 역할 |
|---|---|
| `~/.claude/settings.json` (해당 부분) | 어떤 이벤트에서 어떤 인자로 `notify.sh`를 부를지 정의 |
| `~/.claude/notify.sh` | 호출 시 transcript에서 마지막 assistant 텍스트를 추출하고 OSC 99 페이로드를 사용자 PTS로 송신 |
| `/tmp/claude-notify.log` | 진단용 로그(매 호출마다 fire 시간, transcript 경로, summary, body, 전체 OSC payload의 xxd dump 기록) |

## 동작 흐름

```
Claude Code 이벤트 발생
        │
        ▼
settings.json hook 매칭
        │
        ├─ Stop          → notify.sh "응답 완료"
        ├─ PreToolUse    → notify.sh "질문 대기"   (matcher: AskUserQuestion|ExitPlanMode)
        └─ Elicitation   → notify.sh "입력 요청"
                │
                ▼
        notify.sh 실행
                │
                ├─ stdin JSON 읽기 (transcript_path / session_id)
                ├─ transcript jsonl 후방 검색으로 마지막 assistant.text 추출
                ├─ NL/`;`/BEL/ESC 제거 → awk substr로 100자 truncate
                ├─ BODY = "<basename($PWD)> — <SUMMARY>"
                ├─ /proc/<ppid 체인>/fd/1 walk로 사용자 PTS 탐색
                └─ printf '\033]99;t=Claude Code <msg>;b=<BODY>;i=<id>;ts=<ts>\007' → /dev/pts/N
                        │
                        ▼
                cmux PTY 수신 → toast 표시
```

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
    ]
  }
}
```

### 등록 이벤트와 발화 시점

| 이벤트 | matcher | 발화 시점 | 즉시성 | notify.sh 인자 |
|---|---|---|---|---|
| `PreToolUse` | `AskUserQuestion\|ExitPlanMode` | claude가 사용자에게 질문/플랜 승인을 요청하는 도구를 부르기 직전 | 즉시 | `"질문 대기"` |
| `Stop` | `""` (모든 정지) | claude가 응답을 마치는 시점 | 즉시 | `"응답 완료"` |
| `Elicitation` | `""` | MCP 서버가 elicitation으로 사용자 입력을 요청 | 즉시 | `"입력 요청"` |

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

## OSC 99 페이로드 형식

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
