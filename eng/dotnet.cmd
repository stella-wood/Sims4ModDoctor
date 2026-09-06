@echo off
setlocal
set "PROJECT_ROOT=%~dp0.."
set "DOTNET_CLI_HOME=%PROJECT_ROOT%\.tools-state\dotnet-home"
set "NUGET_PACKAGES=%PROJECT_ROOT%\.tools-state\nuget-packages"
set "DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1"
set "DOTNET_CLI_TELEMETRY_OPTOUT=1"
set "LOCAL_DOTNET=%PROJECT_ROOT%\.tools\dotnet\dotnet.exe"
if exist "%LOCAL_DOTNET%" (
    "%LOCAL_DOTNET%" %*
) else (
    dotnet %*
)
exit /b %ERRORLEVEL%
