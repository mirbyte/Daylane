@echo off
setlocal

cd /d "%~dp0"

set "RID=win-x64"
if not "%~1"=="" set "RID=%~1"

set "OUT=bin\Release\net10.0-windows\%RID%\publish"
set "ARTIFACTS=artifacts"
set "COMMON=/t:Publish /p:Configuration=Release /p:RuntimeIdentifier=%RID% /p:DebugType=none /nologo /v:m"

if not exist "%ARTIFACTS%" mkdir "%ARTIFACTS%"

echo Building release (%RID%)...
echo.

dotnet restore Daylane.csproj -r %RID% --nologo
if errorlevel 1 goto :failed

echo [1/2] Self-contained (includes .NET runtime)...
msbuild Daylane.csproj %COMMON% /p:SelfContained=true /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true /p:PublishDir=%OUT%\self-contained\
if errorlevel 1 goto :failed
call :zip_one "%OUT%\self-contained" "Daylane-%RID%-self-contained"
if errorlevel 1 goto :failed

echo.
echo [2/2] Requires .NET 10 Desktop Runtime...
msbuild Daylane.csproj %COMMON% /p:SelfContained=false /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true /p:PublishDir=%OUT%\runtime\
if errorlevel 1 goto :failed
call :zip_one "%OUT%\runtime" "Daylane-%RID%"
if errorlevel 1 goto :failed

echo.
echo Done:
echo   %ARTIFACTS%\Daylane-%RID%-self-contained.zip
echo   %ARTIFACTS%\Daylane-%RID%.zip
goto :end

:zip_one
set "SRC=%~1"
set "ZIPNAME=%~2"
set "STAGE=%ARTIFACTS%\_stage"
if exist "%STAGE%" rmdir /s /q "%STAGE%"
mkdir "%STAGE%"
copy /y "%SRC%\Daylane.exe" "%STAGE%\Daylane.exe" >nul
if errorlevel 1 exit /b 1
copy /y "%SRC%\config.ini" "%STAGE%\config.ini" >nul
if errorlevel 1 exit /b 1
copy /y "LICENSE" "%STAGE%\LICENSE" >nul
if errorlevel 1 exit /b 1
if exist "%ARTIFACTS%\%ZIPNAME%.zip" del /f /q "%ARTIFACTS%\%ZIPNAME%.zip"
pushd "%STAGE%"
"%SystemRoot%\System32\tar.exe" -a -c -f "..\%ZIPNAME%.zip" Daylane.exe config.ini LICENSE
set "ZIPERR=%ERRORLEVEL%"
popd
rmdir /s /q "%STAGE%"
exit /b %ZIPERR%

:failed
echo.
echo Build failed.

:end
pause
exit /b %errorlevel%
