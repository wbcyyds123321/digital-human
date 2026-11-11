@echo off
chcp 65001 >nul
cd /d "%~dp0"

rem Prefer the project venv, then py -3 / python on PATH.
set "PYEXE="
set "PYARGS="
set "VENV=%~dp0.venv314\Scripts\python.exe"
set "VENV_OLD=%~dp0.venv\Scripts\python.exe"
if exist "%VENV%" (
    set "PYEXE=%VENV%"
) else if exist "%VENV_OLD%" (
    set "PYEXE=%VENV_OLD%"
) else (
    where py >nul 2>nul
    if not errorlevel 1 (
        set "PYEXE=py"
        set "PYARGS=-3"
    ) else (
        set "PYEXE=python"
    )
)

if "%PYEXE%"=="" (
    echo [X] Python not found. Create Tools\Voice\.venv314 or put Python on PATH.
    pause
    exit /b 1
)

echo ============================================
echo  Yixuan 全双工语音服务
echo    python: %PYEXE% %PYARGS%
echo    TTS 合成：8766（可选，Unity 侧可回退）
echo    全双工：8767（麦克风 + VAD + ASR + AEC + 打断）
echo ============================================

rem 8766 未被占用才启动 TTS 合成服务
netstat -ano | findstr ":8766" | findstr "LISTENING" >nul 2>nul
if errorlevel 1 (
    start "Yixuan TTS 8766" "%PYEXE%" %PYARGS% "%~dp0tts_server.py" --port 8766
) else (
    echo [i] 8766 已在运行，跳过 tts_server
)

rem 8767 若已占用则提示
netstat -ano | findstr ":8767" | findstr "LISTENING" >nul 2>nul
if errorlevel 1 (
    echo 启动全双工服务（首次会播放 1.5 秒校准噪声）...
    "%PYEXE%" %PYARGS% "%~dp0voice_server.py" --port 8767
) else (
    echo [i] 8767 已在运行：直接进 Unity Play 即可
)

pause
