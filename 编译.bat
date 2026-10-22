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

"%CSC%" /nologo /utf8output /target:winexe /out:"G49电量助手.exe" /r:System.Windows.Forms.dll /r:System.Drawing.dll "G49电量助手.cs"
if errorlevel 1 (
  echo 编译失败。
  pause
  exit /b 1
)

echo 编译完成：%CD%\G49电量助手.exe
pause
