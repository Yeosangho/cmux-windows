# Claude Code 알림 훅 정리 — Windows 편

Windows에서 직접 띄운 Claude Code의 응답 종료/질문 대기 이벤트를 Windows toast로 받기 위한 hook 모음. Linux용은 상위 `cmux-claude-code/`(README, `notify.sh`, `settings.hooks.json`)에 그대로 남겨둠.

> ⚠ 현재 Windows 측 알림 경로는 **cmuxw에 가지 않고 Windows toast로 직행**합니다. 자세한 사연은 아래 "왜 cmuxw에 안 보내는가" 섹션 참고. cmuxw 본체의 NOTIFY pipe deadlock이 fix되면 다시 cmuxw로 보내는 변종을 추가할 예정입니다.

## 구성

| 파일 | 역할 |
|---|---|
| `%USERPROFILE%\.claude\settings.json` (해당 부분, `settings.hooks.json` 참고) | 어떤 이벤트에서 어떤 인자로 `notify.ps1`을 부를지 정의 |
| `%USERPROFILE%\.claude\notify.ps1` | 호출 시 transcript에서 마지막 assistant 텍스트를 추출하고 `System.Windows.Forms.NotifyIcon` BalloonTip으로 Windows toast 발화 (PowerShell 5.1 native, 외부 모듈/AumID/IPC 없음) |
| `%LOCALAPPDATA%\cmux\claude-notify.log` | 진단용 로그 (매 호출 시각, transcript 경로, summary, body, 송신 결과 기록) |

> Windows의 `~`는 `%USERPROFILE%`(`C:\Users\<user>\`)에 매핑됨. Claude Code 사용자 설정·세션·프로젝트 트리는 모두 `%USERPROFILE%\.claude\` 아래.

## 설치

1. `notify.ps1`을 사용자 홈으로 복사:
   ```powershell
   Copy-Item .\notify.ps1 "$env:USERPROFILE\.claude\notify.ps1"
   ```
2. `%USERPROFILE%\.claude\settings.json`을 열고, `settings.hooks.json`의 `PreToolUse`/`Stop`/`Elicitation` 블록을 최상위 `hooks` 키 아래에 병합한다. 이미 있는 `permissions`/`theme` 등은 그대로 둠.
   - **`<USERPROFILE>`을 실제 홈 경로로 치환할 것.** 예: `C:\\Users\\user`. (JSON 안이라 `\\` 두 번.)
   - 환경변수 그대로 두지 말 것 — Claude Code가 hook 명령을 cmd.exe 경유 없이 PowerShell에 직접 던지기 때문에 `%USERPROFILE%`이 확장되지 않고 리터럴 경로로 해석되어 `-File` 인자가 깨짐 (실측 사례: `Stop hook error: -File 매개 변수에 대한 인수 '%USERPROFILE%\.claude\notify.ps1'을(를) 찾을 수 없습니다.`).
3. PowerShell 실행 정책이 막혀 있으면 hook 명령에 `-ExecutionPolicy Bypass`를 이미 넣어두었으므로 별도 변경 불필요. 그래도 거부되면 사용자 프로필에서 한 번:
   ```powershell
   Set-ExecutionPolicy -Scope CurrentUser RemoteSigned
   ```
4. 반영: 활성 Claude Code 세션은 시작 시점에 `settings.json`을 캐시하므로, 새 세션이거나 `/hooks` 재로드 후에 적용됨.

## 동작 흐름

```
Claude Code 이벤트 발생
        │
        ▼
%USERPROFILE%\.claude\settings.json hook 매칭
        │
        ├─ Stop          → notify.ps1 "응답 완료"
        ├─ PreToolUse    → notify.ps1 "질문 대기"   (matcher: AskUserQuestion|ExitPlanMode)
        └─ Elicitation   → notify.ps1 "입력 요청"
                │
                ▼
        notify.ps1 실행 (powershell.exe -ExecutionPolicy Bypass -NoProfile -File ...)
                │
                ├─ stdin JSON 읽기 (transcript_path / session_id, 2s timeout)
                ├─ transcript jsonl 후방 검색으로 마지막 assistant.text 추출 (300ms race delay)
                ├─ NL/BEL/ESC 제거 → 200자 truncate
                ├─ TITLE = "Claude Code <Msg>"
                ├─ BODY  = "<basename($PWD)> — <SUMMARY>"
                │
                └─ System.Windows.Forms.NotifyIcon BalloonTip 발화
                      → Win10/11이 modern toast 파이프라인으로 변환 → 알림 센터
```

## 왜 cmuxw에 안 보내는가 (2026-05-07 진단)

원래 계획은 Linux의 `notify.sh`처럼 **OSC 99을 cmuxw로 직접 전달**해서 cmuxw의 자체 알림 패널 + 페인 라우팅을 활용하는 거였다. 두 경로 모두 시도했지만 Windows에서 신뢰 가능한 운반이 안 됐다:

1. **CONOUT$ → ConPTY → cmuxw 경로**:
   `[System.IO.File]::Open("CONOUT$", ...)`은 .NET FileStream이 device path를 거부 (`FileStream was asked to open a device that was not a file`). Win32 `CreateFileW`로 SafeFileHandle을 받아 우회해도, **Windows Console host가 OSC 99 sequence의 leading ESC를 strip**한 뒤에 ConPTY로 forward해버림. 결과적으로 cmuxw VtParser는 OSC state에 진입하지 못하고 payload가 raw "]99;t=...;b=..." 텍스트로 화면에 찍힌다.

2. **`\\.\pipe\cmux` NOTIFY 경로**:
   `ReadLine()`으로 daemon ack를 기다리면 deadlock하는 결함이 있어 fire-and-forget(WriteLine + Dispose)으로 바꿨다. 하지만 그 뒤에도 cmuxw 재시작 직후 빈 상태에서 단순 PowerShell `Connect → WriteLine` 한 줄 테스트조차 WriteLine에서 hang했다. cmuxw daemon의 NOTIFY 처리 (Dispatcher.InvokeAsync → HandleNotifyCommand → NotificationService.AddNotification → toast 렌더링) 어딘가에 deadlock이 있는 것으로 추정. 별도의 cmux 본체 fix가 필요한 상태.

→ 둘 다 stop hook 발화 시점에 hang 또는 silent fail로 이어지므로, **현재 Windows hook은 cmuxw IPC 의존성을 완전히 제거**하고 `System.Windows.Forms.NotifyIcon` BalloonTip으로 Windows 자체 알림 시스템에 직접 띄운다. PowerShell 5.1 native라 외부 모듈, AumID 등록, named pipe 모두 불필요하고 cmuxw daemon 상태와 무관하게 동작한다.

cmuxw 본체의 NOTIFY pipe deadlock이 해결되면 cmuxw 라우팅 변종을 다시 추가할 예정.

## settings.hooks.json (관련 부분)

`%USERPROFILE%\.claude\settings.json` 의 `hooks` 블록만 발췌:

```json
{
  "hooks": {
    "PreToolUse": [
      {
        "matcher": "AskUserQuestion|ExitPlanMode",
        "hooks": [
          {
            "type": "command",
            "command": "powershell -ExecutionPolicy Bypass -NoProfile -File \"C:\\Users\\<user>\\.claude\\notify.ps1\" \"질문 대기\""
          }
        ]
      }
    ],
    "Stop": [
      {
        "matcher": "",
        "hooks": [
          {
            "type": "command",
            "command": "powershell -ExecutionPolicy Bypass -NoProfile -File \"C:\\Users\\<user>\\.claude\\notify.ps1\" \"응답 완료\""
          }
        ]
      }
    ],
    "Elicitation": [
      {
        "matcher": "",
        "hooks": [
          {
            "type": "command",
            "command": "powershell -ExecutionPolicy Bypass -NoProfile -File \"C:\\Users\\<user>\\.claude\\notify.ps1\" \"입력 요청\""
          }
        ]
      }
    ]
  }
}
```

> 검증된 사실: Claude Code(Windows)는 hook command를 **cmd.exe 경유 없이 직접 실행**하기 때문에 `%USERPROFILE%`은 확장되지 않는다. PowerShell의 `-File` 인자는 받은 문자열을 그대로 파일 경로로 해석하므로 `'%USERPROFILE%\.claude\notify.ps1'을(를) 찾을 수 없습니다.` 에러가 난다. **반드시 절대 경로**(`C:\Users\<user>\.claude\notify.ps1`)로 박을 것.

## 등록 이벤트 (Linux와 동일 정책)

| 이벤트 | matcher | 발화 시점 | notify.ps1 인자 |
|---|---|---|---|
| `PreToolUse` | `AskUserQuestion\|ExitPlanMode` | claude가 사용자에게 질문/플랜 승인 요청을 호출하기 직전 | `"질문 대기"` |
| `Stop` | `""` (모든 정지) | claude가 응답을 마치는 시점 | `"응답 완료"` |
| `Elicitation` | `""` | MCP 서버가 elicitation으로 사용자 입력을 요청 | `"입력 요청"` |

> 의도적으로 빠진 이벤트:
> - **`Notification`** (= "입력 대기"): 약 60초 idle 후 발화 → 노이즈 많아 제외.
> - **`PermissionRequest`**: 한국어 메시지가 다른 hook과 동일 시점에 함께 떠 중복으로 인식되어 제외.

## notify.ps1 주요 설계 결정

| 항목 | 결정 | 이유 |
|---|---|---|
| 출력 대상 | `System.Windows.Forms.NotifyIcon` + `ShowBalloonTip` | PowerShell 5.1 native API. 외부 모듈, AumID 등록, IPC 모두 불필요. Win10/11이 BalloonTip을 modern toast 파이프라인으로 변환해 알림 센터로 보낸다. 위 "왜 cmuxw에 안 보내는가" 섹션의 이력 참고 — CONOUT$/named pipe 경로 모두 신뢰 불가로 판정. |
| NotifyIcon Dispose 전 800 ms sleep | OS가 BalloonTip을 toast queue에 등록하는 시간을 주기 위함. 즉시 Dispose하면 toast 발생 직전에 사라짐 (Windows.Forms 알려진 동작). |
| stdin timeout | `[Console]::In.ReadToEndAsync()` + `.Wait(2000)` | claude.exe → bash.exe → powershell.exe 같은 chain에서 stdin pipe가 close되지 않고 leaked되는 케이스 회피. timeout 후 빈 입력으로 진행하므로 transcript summary는 못 가져오지만 toast는 directory만으로도 발화. |
| Transcript 위치 | hook stdin JSON의 `transcript_path` 우선, 없으면 `session_id`+cwd 정규화 후보 시도 | 공식 입력에서 직접 받는 게 가장 정확. fallback은 호환용. |
| Path 정규화 | `(Get-Location).Path -replace '[\\/:]', '-'` (`D:\ten1010` → `D--ten1010`) | Claude Code가 프로젝트 디렉토리를 슬래시·콜론을 모두 `-`로 바꿔 버킷팅하는 규칙과 일치. |
| Sleep 300 ms | transcript 읽기 직전 | Stop 훅이 transcript 마지막 라인 flush보다 빠르게 발화해 한 턴 이전 응답이 잡히는 race 회피. Linux 측과 동일 값. |
| Truncate | `Substring(0, 200)` | BalloonTip text 한도 (255자) 안에 들어가도록 여유 있게 자름. .NET `Substring`은 UTF-16 code-unit 기준이라 한국어 BMP에선 surrogate pair 끊을 위험 없음. |
| Sanitize | `-replace "[``r``n``a``e]", ' '` | NL/BEL/ESC 제거 — toast 본문에 control byte가 들어가면 일부 Windows 버전에서 잘려 표시. (OSC 99 시절 `;` strip은 더 이상 필요 없으므로 제거.) |
| 인코딩 | UTF-8 명시 (`[Console]::OutputEncoding`) | transcript jsonl이 UTF-8이라 stdin/Get-Content 양쪽 일관 유지. PowerShell 5.1의 default cp949 동작 차단. |
| 로그 위치 | `%LOCALAPPDATA%\cmux\claude-notify.log` | cmuxw 본체가 이미 같은 폴더 (`%LOCALAPPDATA%\cmux\`)에 settings.json / session.json / cmuxw-toast.log 등을 저장. 운영 파일 한 곳 모음. |

## 진단 / 디버깅

매 호출마다 `%LOCALAPPDATA%\cmux\claude-notify.log`에 다음이 추가됨:

- 발화 시각, msg, pid
- transcript 경로
- summary 문자열
- body 문자열
- shown (BalloonTip 발화 성공 여부)
- err (실패 시 예외 메시지)

빠른 점검 명령:

```powershell
# 실시간 로그 보기
Get-Content "$env:LOCALAPPDATA\cmux\claude-notify.log" -Wait -Tail 20

# 수동 호출 (cmuxw 안의 PowerShell에서)
& "$env:USERPROFILE\.claude\notify.ps1" "수동 테스트"
# → cmuxw에 toast가 떠야 함. 안 뜨면 로그의 wroteVia / conoutErr / pipeErr 확인.
```

## 알려진 이슈 / 주의사항

1. **여러 claude 세션이 동시에 살아 있을 때**: 각 세션은 시작 시점에 settings.json을 캐시. 이후 settings.json을 수정해도 활성 세션은 영향을 받지 않음. 새 세션이거나 `/hooks` 재로드 후 적용됨. (Linux와 동일 — 같은 docs.)
2. **Claude Code가 cmuxw 외부에서 실행될 때**: 현재 알림은 cmuxw에 의존하지 않으므로 어디서 실행하든 동일하게 Windows toast가 발화됨. cmuxw 안에서 띄워도 cmuxw 자체 알림 패널에는 안 들어감 (cmux NOTIFY pipe deadlock fix 전까지).
3. **Stop 훅의 race**: 응답 마지막 텍스트가 transcript에 flush되기 전에 Stop이 발화하면 직전 응답이 추출됨. 현재 `Start-Sleep 300ms`로 회피.
4. **OSC 페이로드 길이**: cmuxw 측 PTY 입력 버퍼 limit 이슈는 Linux/Windows 공통. 본문 100자 truncate는 그 마진 안에 들어가도록 정한 값.
5. **PowerShell 5.x vs 7.x**: `notify.ps1`은 5.1에서도 돌도록 작성 (PowerShell 7 only API 미사용). cmuxw가 띄우는 기본 셸이 `pwsh.exe`라면 그쪽에서도 그대로 동작.
6. **Stop hook hang 진단 history (2026-05-07)**: 처음에는 ① CONOUT$를 `[System.IO.File]::Open` path ctor로 열려 했으나 .NET이 device path를 거부해 항상 fallback으로 떨어졌고, ② fallback에서 daemon 응답을 `ReadLine()`으로 무한 대기하는 구조라 client가 영구 block되며 Claude Code가 "stop hook hang"으로 노출했다. ①을 Win32 `CreateFileW` + `SafeFileHandle`로, ②를 fire-and-forget으로 고친 뒤에는 hang은 사라졌으나 ③ Windows Console host가 OSC 99의 leading ESC를 strip해 cmuxw OSC parser가 인식 못 하고 raw 텍스트가 화면에 찍히는 문제, ④ cmuxw daemon NOTIFY pipe가 단순 PowerShell `Connect → WriteLine` 한 줄에서도 hang하는 문제(daemon 측 deadlock 추정)가 잇따라 드러났다. 결국 cmuxw 운반 경로 전부를 포기하고 NotifyIcon BalloonTip로 Windows 자체 toast에 직행. cmuxw 본체의 NOTIFY deadlock fix는 별도 TODO.
