@echo off
chcp 65001 >nul
title 卸载 Whisper
echo ============================================
echo   WhisperClock - 卸载
echo   将删除:注册项、用户数据(闹钟列表/设置/日志)、程序目录
echo   只想清理注册项?用同目录的 clean.bat
echo ============================================
echo.
pause

echo [1/4] 结束正在运行的闹钟程序...
taskkill /f /im WhisperClock.exe >nul 2>&1
taskkill /f /im AlarmClock.exe >nul 2>&1

echo [2/4] 清理注册项(自启动/AUMID/CLSID)...
call "%~dp0clean.bat" nopause

echo [3/4] 删除用户数据...
rmdir /s /q "%LocalAppData%\AlarmClock" >nul 2>&1

echo [4/4] 卸载完成。程序目录即将自动删除。
start "" /b cmd /c "ping 127.0.0.1 -n 2 >nul & rmdir /s /q ""%~dp0"""
exit /b
