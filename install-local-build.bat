@echo off
rem Packs X16M from source and installs it as the global bitmagic.x16m tool, for testing
rem local changes without waiting on a NuGet publish.
setlocal

set SCRIPT_DIR=%~dp0
set PROJ=%SCRIPT_DIR%X16M\X16M.csproj
set OUT=%SCRIPT_DIR%nupkg-local
for %%I in ("%SCRIPT_DIR%..") do set ROOT=%%~fI
set X16D_RELEASE=%ROOT%\BitMagic.X16Debugger\X16D\bin\Release\net10.0
set RUNNER_RELEASE=%ROOT%\BitMagic.TemplateEngine\BitMagic.TemplateEngine.Runner\bin\Release\net10.0

if exist "%OUT%" rmdir /s /q "%OUT%"
mkdir "%OUT%"

rem Build a Release X16D (native core, ZiModem host, debugger, template runner) following
rem CLAUDE.md's build steps, so the installed tool bundles a debugger that matches the source
rem tree rather than whatever stale build happened to be lying around.
set VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe
if not exist "%VSWHERE%" (
    echo vswhere.exe not found - Visual Studio with the C++ toolchain is needed to build X16D's native core.
    exit /b 1
)
set MSBUILD=
for /f "usebackq delims=" %%M in (`"%VSWHERE%" -latest -prerelease -requires Microsoft.Component.MSBuild -find MSBuild\**\Bin\MSBuild.exe`) do (
    if not defined MSBUILD set "MSBUILD=%%M"
)
if not defined MSBUILD (
    echo MSBuild not found via vswhere.
    exit /b 1
)

echo Building EmulatorCore ...
"%MSBUILD%" "%ROOT%\BitMagic.X16Emulator\X16Emulator\EmulatorCore\EmulatorCore.vcxproj" /p:configuration=Release /p:platform=x64 /v:minimal /nologo
if errorlevel 1 goto :buildfailed

echo Building ZiModem host ...
"%MSBUILD%" "%ROOT%\External\BitMagic.ZiModem\native\ZiModemHost.vcxproj" /p:configuration=Release /p:platform=x64 /v:minimal /nologo
if errorlevel 1 goto :buildfailed

echo Building X16D ...
dotnet build "%ROOT%\BitMagic.X16Debugger\X16D\X16D.csproj" -c Release
if errorlevel 1 goto :buildfailed

echo Building TemplateEngine.Runner ...
dotnet build "%ROOT%\BitMagic.TemplateEngine\BitMagic.TemplateEngine.Runner" -c Release
if errorlevel 1 goto :buildfailed

rem Release builds don't copy the native DLLs or the template runner next to X16D.exe.
copy /y "%ROOT%\BitMagic.X16Emulator\X16Emulator\EmulatorCore\x64\Release\EmulatorCore.dll" "%X16D_RELEASE%\EmulatorCore.dll" >nul
if errorlevel 1 goto :buildfailed
copy /y "%ROOT%\External\BitMagic.ZiModem\build\native\bin\zimodem_host.dll" "%X16D_RELEASE%\zimodem_host.dll" >nul
if errorlevel 1 goto :buildfailed
if not exist "%X16D_RELEASE%\TemplateEngine" mkdir "%X16D_RELEASE%\TemplateEngine"
xcopy /y /e /q "%RUNNER_RELEASE%\*" "%X16D_RELEASE%\TemplateEngine\" >nul
if errorlevel 1 goto :buildfailed

rem Bundle the freshly built X16D so the installed tool works standalone (not just
rem --x16d-host/--x16d-port attach mode).
echo Bundling X16D from %X16D_RELEASE% ...
if exist "%SCRIPT_DIR%x16d-win-x64" rmdir /s /q "%SCRIPT_DIR%x16d-win-x64"
mkdir "%SCRIPT_DIR%x16d-win-x64"
xcopy /y /e /q "%X16D_RELEASE%\*" "%SCRIPT_DIR%x16d-win-x64\" >nul

dotnet pack "%PROJ%" -c Release -o "%OUT%"
if errorlevel 1 (
    echo Pack failed.
    exit /b 1
)

rem Local packs are always version 0.1.0 (no Nerdbank.GitVersioning override here), so if
rem uninstall silently fails - e.g. a running X16M.exe still has the old files locked - install
rem below would otherwise just say "already installed" and quietly leave the OLD build in place.
rem Not swallowing uninstall's output means that failure is visible instead of silent.
dotnet tool uninstall -g bitmagic.x16m
dotnet tool install -g bitmagic.x16m --add-source "%OUT%"
if errorlevel 1 (
    echo.
    echo Install failed - this usually means uninstall above couldn't remove the old files.
    echo Check for a running X16M.exe holding them locked ^(tasklist /FI "IMAGENAME eq X16M.exe"^), close it, and rerun this script.
    exit /b 1
)

call "%SCRIPT_DIR%register-mcp.bat"

endlocal
exit /b 0

:buildfailed
echo X16D build failed.
exit /b 1
