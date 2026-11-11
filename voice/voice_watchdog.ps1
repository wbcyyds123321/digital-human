param(
    [int]$IntervalSeconds = 5
)

$ErrorActionPreference = 'Continue'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$python = @((Join-Path $root '.venv314\Scripts\python.exe'), (Join-Path $root '.venv\Scripts\python.exe')) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $python) { $python = Join-Path $root '.venv\Scripts\python.exe' }
$watchdogLog = Join-Path $root '_voice_watchdog.log'
$ttsOut = Join-Path $root '_tts_watchdog.out.log'
$ttsErr = Join-Path $root '_tts_watchdog.err.log'
$voiceOut = Join-Path $root '_voice_watchdog.out.log'
$voiceErr = Join-Path $root '_voice_watchdog.err.log'

$lastTtsStart = [datetime]::MinValue
$lastVoiceStart = [datetime]::MinValue
$lastState = ''

function Write-WatchdogLog([string]$message) {
    try {
        Add-Content -LiteralPath $watchdogLog -Value ("[{0}] {1}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $message) -Encoding UTF8
    } catch {}
}

function Test-VoiceHealth([int]$port) {
    try {
        $response = Invoke-WebRequest -Uri ("http://127.0.0.1:{0}/health" -f $port) -TimeoutSec 3 -UseBasicParsing
        return ($response.StatusCode -eq 200)
    } catch {
        return $false
    }
}

function Start-VoiceProcess([string]$script, [int]$port, [string]$stdout, [string]$stderr) {
    if (-not (Test-Path -LiteralPath $python)) {
        Write-WatchdogLog "Python venv missing: $python"
        return $false
    }
    try {
        Start-Process -FilePath $python `
            -ArgumentList @($script, '--port', "$port") `
            -WorkingDirectory $root `
            -WindowStyle Hidden `
            -RedirectStandardOutput $stdout `
            -RedirectStandardError $stderr | Out-Null
        Write-WatchdogLog "Started $script on port $port"
        return $true
    } catch {
        Write-WatchdogLog ("Failed to start {0}: {1}" -f $script, $_.Exception.Message)
        return $false
    }
}

Write-WatchdogLog "Voice watchdog started (pid=$PID)"

while ($true) {
    $ttsOk = Test-VoiceHealth 8766
    $voiceOk = Test-VoiceHealth 8767
    $state = "tts=$ttsOk voice=$voiceOk"
    if ($state -ne $lastState) {
        Write-WatchdogLog $state
        $lastState = $state
    }

    $now = Get-Date
    if (-not $ttsOk -and ($now - $lastTtsStart).TotalSeconds -ge 15) {
        $lastTtsStart = $now
        Start-VoiceProcess 'tts_server.py' 8766 $ttsOut $ttsErr | Out-Null
    }
    if (-not $voiceOk -and ($now - $lastVoiceStart).TotalSeconds -ge 20) {
        $lastVoiceStart = $now
        Start-VoiceProcess 'voice_server.py' 8767 $voiceOut $voiceErr | Out-Null
    }

    Start-Sleep -Seconds $IntervalSeconds
}
