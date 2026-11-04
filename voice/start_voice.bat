@echo off
chcp 65001 >nul
cd /d "%~dp0"

rem ---- 选择解释器：优先本目录 venv（完整语音依赖），再退回 DSH python / py -3 ----
set "PYEXE="
set "PYARGS="
set "VENV=%~dp0.venv\Scripts\python.exe"
set "BUNDLED=python"
if exist "%VENV%" (
    set "PYEXE=%VENV%"
) else if exist "%BUNDLED%" (
    set "PYEXE=%BUNDLED%"
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
    echo [X] 找不到 python，请修改本 bat 顶部的 BUNDLED 路径
    pause
    exit /b 1
)

echo ============================================
echo  Yixuan 全双工语音服务
echo    python: %PYEXE% %PYARGS%
echo    TTS 合成：8766（可选，Unity 回退用）
echo    全双工  ：8767（采集 + VAD + ASR + AEC + 打断）
echo ============================================

rem 8766 没被占用才启动 TTS 合成服务
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
