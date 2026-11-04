@echo off
chcp 65001 >nul
cd /d "%~dp0"

rem 优先用 PATH 里的 python；没有就用本机 DSH 自带的解释器
where python >nul 2>nul
if %errorlevel%==0 (
    set "PY=python"
) else (
    set "PY=python"
)

if not exist "%PY%" if "%PY%" neq "python" (
    echo [X] 找不到 python，请修改本 bat 里的 PY 路径
    pause
    exit /b 1
)

echo 启动 Yixuan TTS sidecar（端口 8766）...
"%PY%" "%~dp0tts_server.py" --port 8766
pause
