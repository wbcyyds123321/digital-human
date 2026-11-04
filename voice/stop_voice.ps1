# stop_voice.ps1 — 停止 Yixuan 语音服务（看门狗 + tts_server 8766 + voice_server 8767）
#
# 只杀"本目录"相关的进程，不会误伤别的 python / powershell：
#   1) 命令行里带 voice_watchdog.ps1 的 powershell（看门狗）
#   2) 命令行里带本目录路径(或本目录 .venv)且跑 tts_server.py / voice_server.py 的 python
#   3) 上面这些 python 的子进程（voice_server 会派生辅助解释器）

$ErrorActionPreference = 'SilentlyContinue'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$venvDir = Join-Path $here '.venv'
$all = Get-CimInstance Win32_Process

$targets = New-Object System.Collections.Generic.List[int]

# 1) watchdog
foreach ($p in $all | Where-Object { $_.Name -eq 'powershell.exe' -and $_.CommandLine -and $_.CommandLine -match 'voice_watchdog\.ps1' }) {
    $targets.Add([int]$p.ProcessId)
}

# 2) voice servers started from this folder (the venv python carries the full path in its command line)
$py = @($all | Where-Object {
    $_.Name -eq 'python.exe' -and $_.CommandLine -and
    $_.CommandLine -match '(tts_server|voice_server)\.py' -and
    (($_.CommandLine -match [regex]::Escape($here)) -or ($_.CommandLine -match [regex]::Escape($venvDir)))
})
foreach ($p in $py) { $targets.Add([int]$p.ProcessId) }

# 3) children of those python processes
$parents = @($py | ForEach-Object { [int]$_.ProcessId })
if ($parents.Count -gt 0) {
    foreach ($p in $all | Where-Object { $parents -contains [int]$_.ParentProcessId }) {
        $targets.Add([int]$p.ProcessId)
    }
}

$targets = $targets | Sort-Object -Unique
if ($targets.Count -eq 0) {
    Write-Output '[voice] nothing running.'
    exit 0
}
foreach ($procId in $targets) {
    Stop-Process -Id $procId -Force -ErrorAction SilentlyContinue
}
Write-Output ('[voice] stopped pids: ' + ($targets -join ','))
