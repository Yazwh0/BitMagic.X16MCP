@echo off
rem Installs (or updates) the published bitmagic.x16m .NET tool from NuGet.org.
setlocal

dotnet tool list -g | findstr /I "bitmagic.x16m" >nul
if %errorlevel%==0 (
    dotnet tool update -g bitmagic.x16m
) else (
    dotnet tool install -g bitmagic.x16m
)

call "%~dp0register-mcp.bat"

endlocal
