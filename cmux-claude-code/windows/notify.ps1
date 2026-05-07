# Claude Code 응답 완료 / 질문 대기 / 입력 요청 시 cmuxw에 OSC 99 알림을 보낸다.
#   ex)  notify.ps1 "응답 완료"  →  title = "Claude Code 응답 완료"
#
# Architecture (mirrors the Linux notify.sh design):
#   We write the OSC 99 sequence directly to [Console]::Out. cmuxw is the
#   ConPTY host of the Claude Code session that triggered this hook, so
#   bytes written to our stdout flow through ConPTY into cmuxw's PTY input
#   stream where OscHandler.HandleOsc(99, payload) parses them and calls
#   NotificationService.AddNotification. That gives us:
#     - cmuxw's own notification panel entry (pane-accurate routing)
#     - the Windows toast fired by App.NotificationAdded handler
#     - toast-click → HandleToastNavigate → jumps to the originating pane
#
#   We tried two earlier transports and abandoned both:
#   - CreateFileW("CONOUT$") + FileStream.Write: .NET FileStream's path-based
#     ctor refuses device paths, and even with the SafeFileHandle workaround
#     the bytes don't reach cmuxw (the Console host strips the leading ESC of
#     OSC sequences when written through that path).
#   - `\\.\pipe\cmux` NOTIFY JSON command: client side hangs because
#     StreamWriter with AutoFlush calls FlushFileBuffers, which on a named
#     pipe blocks until the server has drained the byte. With the cmuxw
#     daemon's UI-thread dispatch + this very PS pane's stdout being rendered
#     on the same UI thread, that produces a deadlock specific to hooks
#     running inside the same cmuxw instance.
#
#   [Console]::Write does NOT hit either failure mode — it goes through the
#   normal console output path, which ConPTY forwards verbatim to cmuxw.

param(
    [string]$Msg = "응답 완료"
)

$ErrorActionPreference = 'Continue'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

# ── Read hook stdin JSON (with hard timeout) ───────────────────────────────
$hookInput = ""
if ([Console]::IsInputRedirected) {
    try {
        $task = [Console]::In.ReadToEndAsync()
        if ($task.Wait(2000)) { $hookInput = $task.Result }
    } catch { }
}

# ── Locate transcript jsonl ────────────────────────────────────────────────
$transcript = ""
$sessionId = ""
if ($hookInput) {
    try {
        $j = $hookInput | ConvertFrom-Json
        if ($j.transcript_path) { $transcript = $j.transcript_path }
        if ($j.session_id) { $sessionId = $j.session_id }
    } catch { }
}
if (-not $transcript -and $sessionId) {
    $projDir = ((Get-Location).Path -replace '[\\/:]', '-')
    foreach ($c in @(
        "$env:USERPROFILE\.claude\projects\$projDir\$sessionId.jsonl",
        "$env:USERPROFILE\.claude\projects\$projDir\sessions\$sessionId.jsonl"
    )) {
        if (Test-Path -LiteralPath $c) { $transcript = $c; break }
    }
}

# ── Extract last assistant text from transcript ────────────────────────────
$summary = ""
if ($transcript -and (Test-Path -LiteralPath $transcript)) {
    # Same race window as the Linux notify.sh: Stop fires before the last
    # assistant chunk lands in the jsonl. 300 ms is enough in practice.
    Start-Sleep -Milliseconds 300
    try {
        $lines = Get-Content -LiteralPath $transcript -Encoding UTF8
        for ($i = $lines.Count - 1; $i -ge 0 -and -not $summary; $i--) {
            $line = $lines[$i]
            if (-not $line) { continue }
            try { $obj = $line | ConvertFrom-Json } catch { continue }
            if ($obj.type -ne 'assistant') { continue }
            foreach ($c in @($obj.message.content)) {
                if ($c.type -eq 'text' -and $c.text) {
                    $summary = $c.text
                    break
                }
            }
        }
    } catch { }
}

# ── Sanitize: strip control bytes that would terminate the OSC sequence ────
# `;` is the OSC field separator, `\a` (BEL) is the terminator, `\e` (ESC)
# is the sequence opener — any of these inside the body would prematurely
# end the OSC 99 we're about to emit.
if ($summary) {
    $summary = ($summary -replace "[`r`n;`a`e]", ' ')
    if ($summary.Length -gt 60) { $summary = $summary.Substring(0, 60) }
}

# ── Build title/body ───────────────────────────────────────────────────────
$dir = Split-Path -Leaf (Get-Location).Path
$dir = ($dir -replace "[`r`n;`a`e]", ' ')
$body = if ($summary) { "$dir — $summary" } else { $dir }
# Hard cap on body length. ConPTY can split very long OSC sequences across
# read calls and cmuxw's VtParser then drops the partial sequence, so keep
# the whole emission well under the typical PTY buffer (~256 bytes).
if ($body.Length -gt 90) { $body = $body.Substring(0, 90) }
$title = "Claude Code $Msg"
$title = ($title -replace "[`r`n;`a`e]", ' ')
$ts = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds().ToString()
$id = "$ts-$PID"

# ── Emit OSC 99 directly to stdout (ConPTY → cmuxw VtParser) ───────────────
$e = [char]27
$bel = [char]7
$payload = "$e]99;t=$title;b=$body;i=$id;ts=$ts$bel"

$emitted = $false
$err = ""
$transport = ""

# Mirror the Linux notify.sh trick on Windows: walk the parent process chain
# until we find one whose console is the cmuxw-owned ConPTY (any ancestor
# spawned by cmuxw will inherit it), AttachConsole to that pid, then write
# the OSC 99 bytes directly to CONOUT$. The bytes flow through the
# pseudo-console pipe straight back to cmuxw's VtParser — same path as the
# normal terminal output, no StreamWriter / NamedPipe / stdout-redirect
# involvement.
if (-not ('Cmux.AttachNative' -as [type])) {
    Add-Type -Namespace Cmux -Name AttachNative -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError=true)]
public static extern bool AttachConsole(uint dwProcessId);
[System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError=true)]
public static extern bool FreeConsole();
[System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError=true, CharSet=System.Runtime.InteropServices.CharSet.Unicode)]
public static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(
    string lpFileName,
    uint dwDesiredAccess,
    uint dwShareMode,
    System.IntPtr lpSecurityAttributes,
    uint dwCreationDisposition,
    uint dwFlagsAndAttributes,
    System.IntPtr hTemplateFile);
'@
}

function Try-AttachAndWrite([uint32]$pid_, [byte[]]$bytes) {
    [void][Cmux.AttachNative]::FreeConsole()
    if (-not [Cmux.AttachNative]::AttachConsole($pid_)) { return $false }
    try {
        # GENERIC_WRITE = 0x40000000, FILE_SHARE_READ|WRITE = 3, OPEN_EXISTING = 3
        $h = [Cmux.AttachNative]::CreateFileW("CONOUT$", 0x40000000, 3, [IntPtr]::Zero, 3, 0, [IntPtr]::Zero)
        if (-not $h -or $h.IsInvalid) { return $false }
        try {
            $stream = New-Object System.IO.FileStream($h, [System.IO.FileAccess]::Write)
            $stream.Write($bytes, 0, $bytes.Length)
            $stream.Flush()
            $stream.Close()
            return $true
        } catch {
            return $false
        }
    } finally {
        [void][Cmux.AttachNative]::FreeConsole()
    }
}

try {
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($payload)
    # Build the full ancestor chain first, then pick the ancestor that is the
    # *direct child of cmuxw.exe* — that is the process whose console is the
    # ConPTY cmuxw owns. Picking the first attachable ancestor is wrong: in
    # the hook chain claude.exe owns its own (separate) console and accepts
    # AttachConsole, but bytes written there never reach cmuxw.
    $chain = @()
    $cur = $PID
    for ($i = 0; $i -lt 12; $i++) {
        $info = Get-CimInstance -ClassName Win32_Process -Filter "ProcessId=$cur" -ErrorAction SilentlyContinue
        if ($null -eq $info) { break }
        $chain += [pscustomobject]@{ Pid = [uint32]$cur; Name = $info.Name }
        $parent = [uint32]$info.ParentProcessId
        if ($parent -eq 0) { break }
        $cur = $parent
    }
    $tried = @()
    $targetPid = 0
    # Locate the cmux process that owns the ConPTY (either the GUI cmuxw.exe
    # itself or the cmux-daemon.exe agent that actually does the spawning in
    # production builds). The entry one step *down* from it in the chain is
    # the ConPTY child whose console cmux PTY-reads back to the GUI.
    for ($j = 0; $j -lt $chain.Count; $j++) {
        $n = $chain[$j].Name
        if (($n -ieq 'cmuxw.exe' -or $n -ieq 'cmux-daemon.exe') -and $j -gt 0) {
            $targetPid = $chain[$j - 1].Pid
            break
        }
    }
    if ($targetPid -ne 0) {
        $tried += $targetPid
        if (Try-AttachAndWrite $targetPid $bytes) {
            $emitted = $true
            $transport = "AttachConsole(pid=$targetPid; cmuxw-child)"
        } else {
            $err = "AttachConsole(cmuxw-child pid=$targetPid) failed"
        }
    } else {
        # Fallback: cmuxw.exe not in ancestor chain (manual run from a non-cmuxw
        # shell, or chain too deep). Try every ancestor as before.
        for ($k = 1; $k -lt $chain.Count; $k++) {
            $p = $chain[$k].Pid
            $tried += $p
            if (Try-AttachAndWrite $p $bytes) {
                $emitted = $true
                $transport = "AttachConsole(pid=$p; fallback-walk)"
                break
            }
        }
        if (-not $emitted) {
            $err = "no cmuxw.exe in ancestors; walk also failed; tried: $($tried -join ',')"
        }
    }
    # Stash the chain (pid:name) into err for diagnostics regardless of success.
    if ($chain.Count -gt 0) {
        $chainStr = ($chain | ForEach-Object { "$($_.Pid):$($_.Name)" }) -join ' -> '
        $err = ($err + " | chain: $chainStr").TrimStart(' |')
    }
} catch {
    $err = "attach: $($_.Exception.Message)"
}

# ── Diagnostic log: %LOCALAPPDATA%\cmux\claude-notify.log ──────────────────
$logPath = Join-Path $env:LOCALAPPDATA "cmux\claude-notify.log"
$logDir = Split-Path $logPath
if (-not (Test-Path -LiteralPath $logDir)) {
    New-Item -ItemType Directory -Force -Path $logDir | Out-Null
}
$logLine = @(
    "[$(Get-Date -Format HH:mm:ss.fff)] notify.ps1 fired msg='$Msg' pid=$PID",
    "  transcript=$transcript",
    "  summary='$summary'",
    "  body='$body'",
    "  emitted=$emitted",
    "  transport='$transport'",
    "  err='$err'"
) -join [Environment]::NewLine
Add-Content -LiteralPath $logPath -Value $logLine -Encoding UTF8

if (-not $emitted) { exit 1 } else { exit 0 }
