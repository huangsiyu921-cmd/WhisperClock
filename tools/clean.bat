@echo off
chcp 65001 >nul
title 清理 WhisperClock 注册项
echo ============================================
echo   WhisperClock 注册项清理
echo   将删除:自启动项、Toast 激活注册残留 (AUMID/CLSID)
echo   不影响:用户数据(闹钟/设置/日志)、程序目录
echo ============================================
echo.
if /i not "%1"=="nopause" pause

echo [1/2] 删除自启动项(WhisperClock 与历史 AlarmClock)...
reg delete "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v WhisperClock /f >nul 2>&1
reg delete "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v WhisperClock.Login /f >nul 2>&1
reg delete "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v AlarmClock /f >nul 2>&1
reg delete "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v AlarmClock.Login /f >nul 2>&1

echo [2/2] 清理 Toast 激活注册残留 (AUMID/CLSID)...
rem AUMID:直接在 reg query 输出上过滤(避免 echo %%K ^| 管道对特殊字符路径解析出错)
for /f "delims=" %%K in ('reg query "HKCU\Software\Classes\AppUserModelId" 2^>$null ^| findstr /i /c:"WhisperClock.exe" /c:"AlarmClock.exe"') do reg delete "%%K" /f >nul 2>&1
rem CLSID:查每个子键 LocalServer32 的默认值是否指向本程序 exe
for /f "delims=" %%K in ('reg query "HKCU\Software\Classes\CLSID" 2^>nul') do (
    reg query "%%K\LocalServer32" 2>nul | findstr /i /c:"WhisperClock.exe" /c:"AlarmClock.exe" >nul && reg delete "%%K" /f >nul 2>&1
)

echo.
echo 注册项清理完成。当前版本下次启动时 toolkit 会自动重新注册 AUMID。
if /i not "%1"=="nopause" pause
exit /b