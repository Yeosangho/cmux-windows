# Claude Code SessionStart/SessionEnd → OSC 1338 announce 를 cmuxw 가 PTY-read
# 하는 콘솔(CONOUT$) 로 raw write.
#
# Transport: notify.ps1 의 AttachConsole + CONOUT$ raw byte write 패턴 그대로.
# 다른 점은 transcript 파싱 / summary 추출이 없어 훨씬 짧다는 것 — 이 hook 의
# 유일한 책무는 "이 pane 에 Claude 가 살아있다" 라는 결정적 신호 1 회 emit.
#
# Payload (key=value, ';' 구분):
#   cmux-agent=claude    agent 식별자
#   event=start|end      SessionStart / SessionEnd 구분
#   host=<hostname>      로컬 호스트명
#   pid=<hook pid>       hook 스크립트 PID (디버깅용)
#   sid=<claude sess id> Claude Code session_id (있을 때만)
#   ts=<unix epoch>      발화 시각

param(
    [string]$Event = "start"
)

$ErrorActionPreference = 'Continue'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

# ── Read hook stdin JSON (session_id 만 필요) ─────────────────────────────
$hookInput = ""
if ([Console]::IsInputRedirected) {
    try {
        $task = [Console]::In.ReadToEndAsync()
        if ($task.Wait(800)) { $hookInput = $task.Result }
    } catch { }
}
$sessionId = ""
if ($hookInput) {
    try {
        $j = $hookInput | ConvertFrom-Json
        if ($j.session_id) { $sessionId = $j.session_id }
    } catch { }
}

# ── Build OSC 1338 payload ─────────────────────────────────────────────────
$host_ = $env:COMPUTERNAME
$ts = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds().ToString()
$safeEvent = ($Event -replace "[`r`n;`a`e]", ' ')
$safeHost = ($host_ -replace "[`r`n;`a`e]", ' ')
$safeSid = ($sessionId -replace "[`r`n;`a`e]", ' ')

$e = [char]27
$bel = [char]7
$payload = "$e]1338;cmux-agent=claude;event=$safeEvent;host=$safeHost;pid=$PID;sid=$safeSid;ts=$ts$bel"

# ── AttachConsole + CONOUT$ raw write (notify.ps1 와 동일 정공법) ──────────
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

$emitted = $false
$err = ""
$transport = ""
$bytes = [System.Text.Encoding]::UTF8.GetBytes($payload)

try {
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
    $targetPid = 0
    for ($j = 0; $j -lt $chain.Count; $j++) {
        $n = $chain[$j].Name
        if (($n -ieq 'cmuxw.exe' -or $n -ieq 'cmux-daemon.exe') -and $j -gt 0) {
            $targetPid = $chain[$j - 1].Pid
            break
        }
    }
    if ($targetPid -ne 0) {
        if (Try-AttachAndWrite $targetPid $bytes) {
            $emitted = $true
            $transport = "AttachConsole(pid=$targetPid; cmuxw-child)"
        } else {
            $err = "AttachConsole(cmuxw-child pid=$targetPid) failed"
        }
    } else {
        for ($k = 1; $k -lt $chain.Count; $k++) {
            $p = $chain[$k].Pid
            if (Try-AttachAndWrite $p $bytes) {
                $emitted = $true
                $transport = "AttachConsole(pid=$p; fallback-walk)"
                break
            }
        }
        if (-not $emitted) { $err = "no cmuxw.exe in ancestors" }
    }
    if ($chain.Count -gt 0) {
        $chainStr = ($chain | ForEach-Object { "$($_.Pid):$($_.Name)" }) -join ' -> '
        $err = ($err + " | chain: $chainStr").TrimStart(' |')
    }
} catch {
    $err = "attach: $($_.Exception.Message)"
}

# ── Diagnostic log ─────────────────────────────────────────────────────────
$logPath = Join-Path $env:LOCALAPPDATA "cmux\cmux-announce.log"
$logDir = Split-Path $logPath
if (-not (Test-Path -LiteralPath $logDir)) {
    New-Item -ItemType Directory -Force -Path $logDir | Out-Null
}
$logLine = "[$(Get-Date -Format HH:mm:ss.fff)] cmux-announce.ps1 event=$Event pid=$PID emitted=$emitted transport='$transport' err='$err'"
Add-Content -LiteralPath $logPath -Value $logLine -Encoding UTF8

if (-not $emitted) { exit 1 } else { exit 0 }
