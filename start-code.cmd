@ECHO OFF
SETLOCAL EnableDelayedExpansion

:: This command launches a Visual Studio Code with environment variables required to use a local version of the .NET Core SDK.
:: Set VSCODE_CMD environment variable to use a different VS Code variant (e.g., code-insiders).

IF NOT ["%VSCODE_CMD%"] == [""] GOTO find_vscode

:: Check which VS Code variants are available
SET _has_code=
SET _has_insiders=
FOR /f "delims=" %%a IN ('where.exe code 2^>nul') DO @SET _has_code=1
FOR /f "delims=" %%a IN ('where.exe code-insiders 2^>nul') DO @SET _has_insiders=1

IF DEFINED _has_code IF DEFINED _has_insiders (
    echo Both 'code' and 'code-insiders' are installed.
    echo   1^) code
    echo   2^) code-insiders
    SET /P _choice="Select [1]: "
    IF NOT DEFINED _choice SET _choice=1
    IF "!_choice!"=="1" SET VSCODE_CMD=code& GOTO find_vscode
    IF "!_choice!"=="2" SET VSCODE_CMD=code-insiders& GOTO find_vscode
    echo [ERROR] Invalid selection.
    exit /b 1
)

IF DEFINED _has_code SET VSCODE_CMD=code& GOTO find_vscode
IF DEFINED _has_insiders SET VSCODE_CMD=code-insiders& GOTO find_vscode

echo [ERROR] Neither 'code' nor 'code-insiders' is installed or can't be found.
echo.
exit /b 1

:find_vscode
FOR /f "delims=" %%a IN ('where.exe %VSCODE_CMD% 2^>nul') DO @SET vscode=%%a& GOTO break
:break

IF ["%vscode%"] == [""] (
    echo [ERROR] %VSCODE_CMD% is not installed or can't be found.
    echo.
    exit /b 1
)

:: This tells .NET Core to use the same dotnet.exe that build scripts use
SET DOTNET_ROOT=%~dp0.dotnet
SET DOTNET_ROOT(x86)=%~dp0.dotnet\x86

:: This tells .NET Core not to go looking for .NET Core in other places
SET DOTNET_MULTILEVEL_LOOKUP=0

:: Put our local dotnet.exe on PATH first so Visual Studio knows which one to use
SET PATH=%DOTNET_ROOT%;%PATH%

IF NOT EXIST "%DOTNET_ROOT%\dotnet.exe" (
    echo [ERROR] .NET SDK has not yet been installed. Run %~dp0restore.cmd to install.
    echo.
    exit /b 1
)

IF ["%~1"] == [""] GOTO noargs
"%vscode%" %*
exit /b 1

:noargs
"%vscode%" "."