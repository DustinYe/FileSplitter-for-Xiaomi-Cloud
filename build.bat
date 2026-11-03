@echo off
rem Build FileSplitter.exe with the C# compiler that ships with Windows (.NET Framework 4.x).
rem No Visual Studio / SDK required.
setlocal
cd /d "%~dp0"
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist dist mkdir dist
"%CSC%" /nologo /target:winexe /codepage:65001 /optimize+ /win32manifest:src\app.manifest /out:dist\FileSplitter.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll src\Engine.cs src\Theme.cs src\MainForm.cs
if errorlevel 1 (echo BUILD FAILED & exit /b 1)
echo OK: dist\FileSplitter.exe
