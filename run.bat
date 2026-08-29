@echo off
setlocal
cd /d "%~dp0"
where dotnet >nul 2>&1
if errorlevel 1 (
  echo .NET 8 SDK が見つかりません。
  echo https://dotnet.microsoft.com/download/dotnet/8.0 からインストールしてください。
  pause
  exit /b 1
)
dotnet restore
if errorlevel 1 goto :error
dotnet run
exit /b %errorlevel%
:error
echo.
echo 起動準備に失敗しました。
pause
exit /b 1
