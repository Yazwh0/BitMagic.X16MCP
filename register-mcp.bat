@echo off
rem Shared by install-from-nuget.bat and install-local-build.bat. Re-registers "x16m" with
rem Claude Code after a (re)install so a stale cached connection can't linger - MCP tool
rem schemas are fetched once when a server connects, not re-polled, so a session that's
rem already connected won't see a rebuilt binary until Claude Code drops and reconnects it.
rem
rem Registers the PATH-resolvable command "x16m", not an absolute .store\...\X16M.exe path:
rem dotnet tool install/update always keeps that shim pointing at whatever's newest, so this
rem registration never needs touching again across future reinstalls.
where claude >nul 2>nul
if errorlevel 1 (
    echo claude CLI not found on PATH - skipping MCP registration.
    exit /b 0
)

for %%S in (local project user) do claude mcp remove x16m -s %%S >nul 2>nul

claude mcp add x16m -s user -- x16m
