@echo off
setlocal
cd /d "%~dp0"
C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe /nologo /platform:x86 /optimize+ /out:I6Pull.exe src\I6Pull.cs
if errorlevel 1 exit /b 1
echo built I6Pull.exe
