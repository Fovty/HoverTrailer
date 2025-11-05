@echo off
REM HoverTrailer Plugin Deployment Script for Windows
REM This script builds and deploys the plugin to a remote Jellyfin server

REM Load configuration from VS Code settings and .env file
if exist .vscode\.env (
    for /f "delims=" %%a in (.vscode\.env) do set "%%a"
)

set PLUGIN_NAME=Fovty.Plugin.HoverTrailer
set REMOTE_HOST=%JELLYFIN_REMOTE_HOST%
set REMOTE_USER=%JELLYFIN_REMOTE_USER%
set REMOTE_PATH=%JELLYFIN_REMOTE_PATH%
set BUILD_CONFIG=Debug
set TARGET_FRAMEWORK=net9.0
set BUILD_OUTPUT=./Fovty.Plugin.HoverTrailer/bin/Debug/net9.0/publish
set PROJECT_FILE=./Fovty.Plugin.HoverTrailer.sln

echo === HoverTrailer Plugin Deployment ===

:main
if "%1"=="" goto end
if /i "%1"=="clean" call :clean_build
if /i "%1"=="build" call :build_plugin
if /i "%1"=="deploy" call :deploy_plugin
if /i "%1"=="restart" call :restart_jellyfin
shift
goto main

:clean_build
echo Cleaning previous build...
dotnet clean "%PROJECT_FILE%" -c "%BUILD_CONFIG%"
if errorlevel 1 ( echo ❌ Clean failed & pause & exit /b 1 )
echo ✅ Clean completed
goto :eof

:build_plugin
echo Building plugin...
dotnet publish "%PROJECT_FILE%" -c "%BUILD_CONFIG%" -f "%TARGET_FRAMEWORK%" --property:TreatWarningsAsErrors=false /property:GenerateFullPaths=true /consoleloggerparameters:NoSummary
if errorlevel 1 ( echo ❌ Build failed & pause & exit /b 1 )
echo ✅ Build completed
goto :eof

:deploy_plugin
echo Deploying to remote server...
if not defined JELLYFIN_REMOTE_PASSWORD (
    set /p JELLYFIN_REMOTE_PASSWORD="Enter password for %REMOTE_USER%@%REMOTE_HOST%: "
)
where pscp >nul 2>nul
if %errorlevel% neq 0 (
    echo pscp from PuTTY suite not found. Please install it and ensure it's in your PATH.
    echo You can download it from: https://www.chiark.greenend.org.uk/~sgtatham/putty/latest.html
    pause & exit /b 1
)
plink -ssh -pw %JELLYFIN_REMOTE_PASSWORD% %REMOTE_USER%@%REMOTE_HOST% "mkdir -p '%REMOTE_PATH%'"
if errorlevel 1 ( echo ❌ Failed to create remote directory & pause & exit /b 1 )
pscp -pw %JELLYFIN_REMOTE_PASSWORD% -scp "%BUILD_OUTPUT%\%PLUGIN_NAME%.dll" "%REMOTE_USER%@%REMOTE_HOST%:%REMOTE_PATH%/"
if errorlevel 1 ( echo ❌ Deployment failed & pause & exit /b 1 )
echo ✅ Deployment completed
goto :eof

:restart_jellyfin
echo Restarting Jellyfin service...
plink -ssh -pw %JELLYFIN_REMOTE_PASSWORD% %REMOTE_USER%@%REMOTE_HOST% "systemctl restart jellyfin"
if errorlevel 1 ( echo ⚠️ Failed to restart Jellyfin service ) else ( echo ✅ Jellyfin service restarted )
goto :eof

:end
echo 🎉 Script finished.
pause