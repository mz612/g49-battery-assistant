@echo off
setlocal
chcp 65001 >nul
cd /d "%~dp0"

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  echo 未找到 .NET Framework C# 编译器。
  pause
  exit /b 1
)

set "ICON_ARG="
if exist "app.ico" set "ICON_ARG=/win32icon:"app.ico""

"%CSC%" /nologo /utf8output /target:winexe %ICON_ARG% /out:"红龙G49 PRO鼠标电量助手.exe" /r:System.Windows.Forms.dll /r:System.Drawing.dll "G49电量助手.cs"
if errorlevel 1 (
  echo 编译失败。
  pause
  exit /b 1
)

echo 编译完成：%CD%\红龙G49 PRO鼠标电量助手.exe
pause
