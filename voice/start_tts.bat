@echo off
chcp 65001 >nul
cd /d "%~dp0"

rem Prefer the project venv, then a system Python on PATH.
set "VENV314=%~dp0.venv314\Scripts\python.exe"
set "VENV=%~dp0.venv\Scripts\python.exe"
set "PY="
set "PYARGS="
if exist "%VENV314%" (
    set "PY=%VENV314%"
) else if exist "%VENV%" (
    set "PY=%VENV%"
) else (
    where python >nul 2>nul
    if not errorlevel 1 (
        set "PY=python"
    ) else (
        where py >nul 2>nul
        if not errorlevel 1 (
            set "PY=py"
            set "PYARGS=-3"
        ) else (
            echo [X] Python not found. Create Tools\Voice\.venv314 or put Python on PATH.
            pause
            exit /b 1
        )
    )
)

if "%PY%"=="python" goto runpy
if "%PY%"=="py" goto runpy
if not exist "%PY%" (
    echo [X] Python not found. Create Tools\Voice\.venv314 or put Python on PATH.
    pause
    exit /b 1
)

:runpy
echo 启动 Yixuan TTS sidecar（端口 8766）...
"%PY%" %PYARGS% "%~dp0tts_server.py" --port 8766
pause
