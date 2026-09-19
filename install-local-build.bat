@echo off
rem Packs X16M from source and installs it as the global bitmagic.x16m tool, for testing
rem local changes without waiting on a NuGet publish.
setlocal

set SCRIPT_DIR=%~dp0
set PROJ=%SCRIPT_DIR%X16M\X16M.csproj
set OUT=%SCRIPT_DIR%nupkg-local
set X16D_RELEASE=%SCRIPT_DIR%..\BitMagic.X16Debugger\X16D\bin\Release\net10.0

if exist "%OUT%" rmdir /s /q "%OUT%"
mkdir "%OUT%"

rem Bundle a locally built X16D if one is sitting where CLAUDE.md's build steps leave it, so
rem the installed tool works standalone (not just --x16d-host/--x16d-port attach mode).
if exist "%X16D_RELEASE%\X16D.exe" (
    echo Bundling locally built X16D from %X16D_RELEASE% ...
    if exist "%SCRIPT_DIR%x16d-win-x64" rmdir /s /q "%SCRIPT_DIR%x16d-win-x64"
    mkdir "%SCRIPT_DIR%x16d-win-x64"
    xcopy /y /e /q "%X16D_RELEASE%\*" "%SCRIPT_DIR%x16d-win-x64\" >nul
) else (
    echo No Release X16D build found at %X16D_RELEASE% - packing without a bundled debugger.
    echo Point x16m at one with --x16d-host/--x16d-port, --x16d, or X16D_PATH instead.
)

dotnet pack "%PROJ%" -c Release -o "%OUT%"
if errorlevel 1 (
    echo Pack failed.
    exit /b 1
)

dotnet tool uninstall -g bitmagic.x16m >nul 2>nul
dotnet tool install -g bitmagic.x16m --add-source "%OUT%"

endlocal
