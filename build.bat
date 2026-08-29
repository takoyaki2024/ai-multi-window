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
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true
if errorlevel 1 goto :error
echo.
echo 完了: bin\Release\net8.0-windows\win-x64\publish\AiMultiWindow.exe
pause
exit /b 0
:error
echo.
echo ビルドに失敗しました。
pause
exit /b 1
