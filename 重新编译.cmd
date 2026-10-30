@echo off
chcp 65001 >nul
cd /d "%~dp0"
"%WINDIR%\Microsoft.NET\Framework\v4.0.30319\MSBuild.exe" RfidSmartStore.Local.csproj /t:Rebuild /p:Configuration=Release /p:Platform=x86 /v:minimal
if errorlevel 1 (
  echo 编译失败。
) else (
  echo 编译成功：bin\Release\RfidSmartStore.RealReader.V3.exe
)
pause

