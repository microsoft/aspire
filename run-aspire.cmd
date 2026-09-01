@echo off
setlocal

call "%~dp0dotnet.cmd" run --project "%~dp0src\Aspire.Cli\Aspire.Cli.csproj" --no-launch-profile -- %*
exit /b %ERRORLEVEL%
