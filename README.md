# cmux for Windows

A dark, keyboard-first terminal multiplexer for Windows, inspired by tmux/cmux workflows but built natively with WPF + ConPTY.

> **Forked from** https://github.com/mkurman/cmux-windows

---

## Fork에서 추가/개선된 기능 (한국어)

원본 [mkurman/cmux-windows](https://github.com/mkurman/cmux-windows) 대비 다음 기능이 추가되거나 개선되었습니다.

### 1. 한국어 / CJK 입출력 정상화
- **East Asian Width 기반 셀 폭 계산** — CJK 글자가 2셀 폭으로 올바르게 그려져 글자 겹침 / 띄어쓰기 깨짐 해소 (`Cmux.Core/Terminal/UnicodeWidth.cs` 신설, `TerminalBuffer`의 wide cell + continuation cell 처리)
- **한글 IME 조합 정상화** — WPF `TextBox`를 숨김 IME 프록시로 두고 `TerminalControl`은 순수 렌더만 담당. 조합 중 글자는 preedit 오버레이로 표시. `TextBox.Text`를 절대 건드리지 않아 TSF 상태 보존 ("안녕" → "아ㄴ녀ㅇ" 같은 자모 분리 증상 제거)
- **OSC 알림 본문 UTF-8 정상 디코드** — `VtParser`가 OSC 페이로드를 byte 버퍼로 누적 후 dispatch 시점 일괄 UTF-8 디코드
- **OSC 0x9C ↔ UTF-8 충돌 수정** — "시"(EC 8B 9C) 같이 0x9C를 포함한 문자가 ST 종료자로 오인되어 알림이 "메�"로 잘리던 문제 수정

### 2. 알림 시스템 강화
- **OSC 99 sender id / timestamp 키 지원** (`i=`, `ts=`) — hook이 동일 알림을 중복 발사해도 dedup 키로 중복 제거
- **알림 사운드** (`winmm.dll` `PlaySound`) — toast 외에 인앱 사운드도 함께 재생
- **알림 로컬 시각 표시** — sender 타임스탬프를 받아 로컬 TZ로 환산해 패널 / toast에 노출
- **단일 인스턴스 toast forwarding** — toast 클릭으로 새 cmux 프로세스가 기동되더라도 기존 인스턴스로 인자가 전달되어 중복 실행 방지
- **NotificationPanel 클릭 → 해당 패널 점프** (`NotificationClicked` 이벤트 + `MainViewModel.NavigateToNotification`)

> **Claude Code 연동 설정 방법은 [`cmux-claude-code/README.md`](cmux-claude-code/README.md) 참고.**
> `~/.claude/settings.json`의 `hooks` 블록과 OSC 99 페이로드를 PTS로 송신하는 `notify.sh` 예시,
> 등록 이벤트(`Stop` / `PreToolUse` / `Elicitation`)별 발화 시점, 진단 로그(`/tmp/claude-notify.log`) 활용법까지 포함되어 있습니다.

### 3. 활성 패널 시각화
- 키보드 입력이 전달되는 패널에 **2px 파란색(`#3B82F6`) 외곽선** 표시
- Border를 패널별 캐시 (`_paneBorderCache`) 해 포커스 변화 시 색만 즉시 교체 — 리빌드 / 레이아웃 시프트 없음
- 새 테마 리소스: `FocusedPaneBorderColor`, `FocusedPaneBorderBrush`

### 4. 레이아웃 / 세션 안정화
- **레이아웃 변경 시 세션 파괴 안 됨** — `SurfaceViewModel.ApplyLayoutTree`가 기존 패널 ID를 비파괴적으로 재매핑
- **세션 복원 시 자동 Enter 입력 제거** — 재시작 후 "이전 세션의 다음 추천 메시지가 그냥 실행되던" 증상 해소
- **레이아웃 변경 후 한국어 입력 안 되던 문제** — `Focus()` → `Keyboard.Focus(imeProxy)` 2-stage focus dance + `InputMethod.SetIsInputMethodEnabled` 재assert로 IME 컨텍스트 재바인딩

### 회귀 테스트
- `tests/Cmux.Tests/CoreTests.cs`에 OSC 0x9C UTF-8 충돌, 알림 dedup, OSC 99 sender id/timestamp 파싱 등 회귀 테스트 추가

### 5. 워크스페이스 폴더 트리 + 외부 에디터 연동
- **워크스페이스 사이드바 inline 폴더 트리** — 각 워크스페이스 항목 안에 로컬 / 원격(SSH) 폴더 등록 가능. 사이드바 헤더 컨텍스트 메뉴의 `Add Folder...`로 추가, TreeView 컨텍스트 메뉴로 Refresh / Remove root folder.
- **다크 테마 TreeView template** — IsSelected/IsMouseOver hover 색, 자체 expander chevron, 깊이별 indent + 긴 이름 ellipsis, 자체 viewport (MaxHeight 360) + 가로/세로 스크롤바 + 마우스 tilt-wheel(WM_MOUSEHWHEEL) 가로 스크롤.
- **`Open in Cursor` / `Open in VSCode`** — 폴더/파일 더블클릭 또는 컨텍스트 메뉴로 외부 에디터를 `--new-window`로 launch. 원격 폴더는 `--remote ssh-remote+<alias>`로 Remote-SSH 위임. cmuxw 자체 에디터는 더 이상 사용하지 않고 git/IntelliSense/port-forward 등은 Cursor/VSCode가 담당.
- **`AddEditorFolderWindow`** — `~/.ssh/config`의 host alias를 ComboBox로 표시(`SshConfigParser`). alias 선택 시 user/port/identity는 ssh_config에 위임하고 PasswordBox만 활성(키 passphrase / 비밀번호 모두 입력 가능). Manual entry 모드에서는 host/port/user/key/password 직접 입력.
- **원격 SFTP transport는 OpenSSH `sftp.exe` 호출** (`OpenSshSftpService`) — `sftp.exe -b -`로 list/get/put 수행하므로 ssh_config의 ProxyJump / ProxyCommand / Match / Include / IdentityAgent 모두 그대로 동작. 비밀번호 / 키 passphrase는 SSH_ASKPASS helper 스크립트 + `SSH_ASKPASS_REQUIRE=force`로 비대화식 전달, OutputEncoding cp949로 인한 한국어 깨짐 회피.
- **다크 시인성 보강** — `DarkTheme.xaml`에 implicit `ScrollBar` / `ComboBox` / `ComboBoxItem` / `PasswordBox` 스타일 추가. 다이얼로그와 TreeView 등 보조 컨트롤도 라이트 시스템 색이 새어 들어오지 않게 통일.

### 6. Windows용 Claude Code hook 알림 (cmux-claude-code/windows/)
- **신규 디렉토리** `cmux-claude-code/windows/` — README + `notify.ps1` + `settings.hooks.json` 템플릿. Windows의 Claude Code가 cmuxw 안에서 동작할 때 Stop / PreToolUse(`AskUserQuestion`/`ExitPlanMode`) / Elicitation 이벤트마다 cmuxw에 OSC 99 toast를 발화하도록 구성하는 Windows-side 자산.
- **AttachConsole + CONOUT$ raw write 트랜스포트** — Linux 측 `notify.sh`의 `/proc/<ppid>/fd/1` walk와 등가. notify.ps1이 부모 process chain을 walk해 `cmux-daemon.exe`(또는 `cmuxw.exe`)를 찾고 그 직전 ancestor(ConPTY child PowerShell)에 `AttachConsole`한 뒤 `CreateFileW("CONOUT$")`로 raw UTF-8 OSC 99 byte를 write. hook stdout이 Claude Code에 capture되는 한계, NamedPipe NOTIFY 측 hang, PowerShell `OutputEncoding` cp949로 인한 한국어 깨짐을 모두 회피.
- 진단 로그는 `%LOCALAPPDATA%\cmux\claude-notify.log`에 기록(시각, msg, transcript, summary, body, transport, 시도한 ancestor pid:name chain).

---

## Why / Who / What / How

| Why (problem) | Who (for) | What (feature) | How to use |
|---|---|---|---|
| You lose context across projects and shells | Developers juggling many repos/tasks | **Workspaces + surfaces (tabs)** | `Ctrl+N` new workspace, `Ctrl+T` new surface, switch with `Ctrl+1..9` |
| One terminal is never enough | CLI-heavy users, agent workflows | **Split panes** (right/down) | `Ctrl+D` split right, `Ctrl+Shift+D` split down, `Ctrl+Alt+Arrow` focus pane |
| You miss important agent outputs | AI-assisted coding users (Claude/Codex/etc.) | **OSC notifications + unread tracking** | `Ctrl+I` open notifications, `Ctrl+Shift+U` jump to latest unread |
| You need auditability of executed commands | Security-conscious / debugging workflows | **Command logs + history picker** | `Ctrl+Shift+L` logs, `Ctrl+Alt+H` command history, insert/run from UI |
| You want full session recall after crashes/restarts | Long-running sessions | **Session persistence + transcript capture** | Auto restore on startup + open **Session Vault** (`Ctrl+Shift+V`) |
| You want searchable output history like Termius vault | Anyone reviewing terminal sessions | **Session Vault browser** | Open vault, filter captures, preview transcript, copy/open file |
| You need dark theme consistency and personalization | Users who care about UX/readability | **Dark UI + terminal theme customization** | Settings (`Ctrl+,`) for colors/font/cursor + workspace accents |
| You want quick actions without mouse hunting | Keyboard-first power users | **Command palette + shortcuts** | `Ctrl+Shift+P` command palette, menu mirrors key flows |
| You need automation from scripts/tools | Integrators/agent hooks | **Named pipe CLI API** (`cmux`) | `cmux notify`, `cmux workspace`, `cmux split`, `cmux status` |

---

## Core capabilities

- Native **ConPTY terminal emulation** (real Windows terminal backend)
- Workspace sidebar with metadata (git branch, cwd, notifications)
- Multi-surface tabs and split-pane layout management
- Notification ingestion (OSC 9/99/777) for coding agents
- Command logs/history with filtering and quick replay
- Terminal transcript capture + Session Vault browsing
- Persistent sessions (window + workspace/surface/pane state)
- Dark desktop UI with keyboard-first navigation

---

## Screenshots

<details>
  <summary>Open screenshots</summary>

  <p><strong>Main workspace view</strong></p>
  <img src="assets/screenshots/1.jpg" alt="cmux main workspace" width="1000" />

  <p><strong>Snippets panel</strong></p>
  <img src="assets/screenshots/2.jpg" alt="cmux snippets panel" width="700" />

  <p><strong>Command logs window</strong></p>
  <img src="assets/screenshots/3.jpg" alt="cmux command logs" width="1000" />
</details>

---

## Build and run (Windows)

### Requirements

- Windows 10/11
- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- Optional: Visual Studio 2022 / Build Tools

### Clone

```powershell
git clone <repo-url> cmux-windows
cd cmux-windows
```

### Dev run

```powershell
dotnet build Cmux.sln -c Debug
dotnet run --project src/Cmux/Cmux.csproj -c Debug
```

---

## Build `.exe` on Windows

### 1) Framework-dependent `.exe` (smallest output)

```powershell
dotnet publish src/Cmux/Cmux.csproj -c Release -r win-x64 --self-contained false -o publish/cmux-win-x64
```

Output:
- `publish/cmux-win-x64/cmuxw.exe`

Use this when target machines already have .NET runtime installed.

### 2) Self-contained `.exe` (no runtime install needed)

```powershell
dotnet publish src/Cmux/Cmux.csproj -c Release -r win-x64 --self-contained true -o publish/cmux-win-x64-sc
```

Output:
- `publish/cmux-win-x64-sc/cmuxw.exe`

### 3) Single-file self-contained `.exe` (portable artifact)

```powershell
dotnet publish src/Cmux/Cmux.csproj -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true /p:PublishTrimmed=false -o publish/cmux-win-x64-single
```

Output:
- `publish/cmux-win-x64-single/cmuxw.exe`

> Note: WebView2-backed features may require WebView2 Runtime depending on target system state.

### Build CLI executable

```powershell
dotnet publish src/Cmux.Cli/Cmux.Cli.csproj -c Release -r win-x64 --self-contained true -o publish/cmux-cli
```

Add `publish/cmux-cli` to `PATH` to use `cmux` globally.

---

## First 5 minutes (how to use)

1. Launch `cmuxw.exe`
2. `Ctrl+N` to create a workspace for your repo
3. `Ctrl+T` to create additional surfaces (tabs)
4. Split panes with `Ctrl+D` / `Ctrl+Shift+D`
5. Open command palette with `Ctrl+Shift+P` for quick actions
6. Open logs with `Ctrl+Shift+L`
7. Open Session Vault with `Ctrl+Shift+V`
8. Open settings with `Ctrl+,` and tune terminal theme/font/cursor

---

## Keyboard shortcuts

### Workspaces

| Shortcut | Action |
|---|---|
| `Ctrl+N` | New workspace |
| `Ctrl+1..8` | Jump to workspace 1..8 |
| `Ctrl+9` | Jump to last workspace |
| `Ctrl+Shift+W` | Close workspace |
| `Ctrl+Shift+R` | Rename workspace |
| `Ctrl+B` | Toggle sidebar |

### Surfaces (tabs)

| Shortcut | Action |
|---|---|
| `Ctrl+T` | New surface |
| `Ctrl+W` | Close surface |
| `Ctrl+Shift+]` | Next surface |
| `Ctrl+Shift+[` | Previous surface |
| `Ctrl+Tab` / `Ctrl+Shift+Tab` | Cycle surfaces |

### Panes

| Shortcut | Action |
|---|---|
| `Ctrl+D` | Split right |
| `Ctrl+Shift+D` | Split down |
| `Ctrl+Alt+Arrow` | Focus adjacent pane |
| `Ctrl+Shift+Z` | Zoom/unzoom pane |

### Productivity

| Shortcut | Action |
|---|---|
| `Ctrl+Shift+P` | Command palette |
| `Ctrl+Shift+F` | Search overlay |
| `Ctrl+Shift+L` | Command logs |
| `Ctrl+Shift+V` | Session vault |
| `Ctrl+Alt+H` | Command history picker |
| `Ctrl+,` | Settings |

---

## CLI usage

```powershell
# Send a notification (e.g., from agent hooks)
cmux notify --title "Claude Code" --body "Waiting for input"

# Workspace management
cmux workspace list
cmux workspace create --name "My Project"
cmux workspace select --index 0

# Surface/pane actions
cmux surface create
cmux split right
cmux split down

# Inspect status
cmux status
```

---

## Architecture (high level)

```text
src/
  Cmux/         WPF desktop app (views, controls, themes)
  Cmux.Core/    terminal engine, models, services, persistence, IPC
  Cmux.Cli/     command-line client for automation
tests/
  Cmux.Tests/   unit tests
```

---

## License

MIT
