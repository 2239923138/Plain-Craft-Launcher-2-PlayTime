@echo off
setlocal EnableExtensions

rem ============================================================
rem  PCL build script
rem
rem  Usage:  build.bat [Debug^|Release^|Snapshot]    (default: Release)
rem  Output: dist\<Config>\Plain Craft Launcher 2.exe
rem
rem  Steps: restore NuGet -^> build with VS MSBuild -^> copy to dist
rem
rem  NOTE: keep this file ASCII-only. cmd.exe reads .bat with the
rem  console code page (936 on zh-CN), so UTF-8 Chinese in a BOM-less
rem  .bat corrupts the following commands.
rem ============================================================

cd /d "%~dp0"

set "CONFIG=%~1"
if "%CONFIG%"=="" set "CONFIG=Release"

echo.
echo ============================================================
echo   PCL build  --  configuration: %CONFIG%
echo ============================================================
echo.

rem ---------- 1. locate MSBuild from Visual Studio ----------
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if not exist "%VSWHERE%" (
    echo [ERROR] vswhere.exe not found: "%VSWHERE%"
    echo         Make sure Visual Studio is installed.
    goto :fail
)

set "VSPATH="
for /f "usebackq tokens=*" %%i in (`"%VSWHERE%" -latest -products * -requires Microsoft.Component.MSBuild -property installationPath 2^>nul`) do set "VSPATH=%%i"

if not defined VSPATH (
    echo [ERROR] vswhere found no Visual Studio install with the MSBuild component.
    goto :fail
)

set "MSBUILD=%VSPATH%\MSBuild\Current\Bin\MSBuild.exe"
if not exist "%MSBUILD%" (
    echo [ERROR] MSBuild.exe not found: "%MSBUILD%"
    goto :fail
)

echo [env] Visual Studio : %VSPATH%
echo [env] MSBuild       : %MSBUILD%
echo.

rem ---------- 2. restore NuGet ----------
echo [1/3] Restoring NuGet packages ...
dotnet restore "PCLCS\PCLCS.csproj" --nologo -v quiet
rem Exact string comparison, not `if errorlevel 1`: a crashed tool can return a
rem large negative exit code, and `if errorlevel 1` evaluates that as SUCCESS.
if not "%ERRORLEVEL%"=="0" (
    echo [ERROR] NuGet restore failed.
    goto :fail
)

rem Shut down any lingering build-server node. A stale one makes MSBuild die
rem with a different unrelated startup error on every run (MC1000,
rem MissingMethodException, MSB4136, MSB1025, MSB4184 ...).
dotnet build-server shutdown >nul 2>&1

rem ---------- 3. build ----------
echo [2/3] Building main project ...

rem Delete the previous output BEFORE building. MSBuild can die without a
rem trustworthy exit code, and without this the script would happily copy a
rem STALE exe and print BUILD OK -- a false success that hides a broken build.
if exist "Plain Craft Launcher 2\obj\%CONFIG%\Plain Craft Launcher 2.exe" del /q "Plain Craft Launcher 2\obj\%CONFIG%\Plain Craft Launcher 2.exe"
if exist "Plain Craft Launcher 2\bin\Plain Craft Launcher 2.exe" del /q "Plain Craft Launcher 2\bin\Plain Craft Launcher 2.exe"

rem nodeReuse:false avoids a flaky MC2000 error caused by MSBuild node reuse
"%MSBUILD%" "Plain Craft Launcher 2\Plain Craft Launcher 2.vbproj" ^
    -p:Configuration=%CONFIG% -m -nodeReuse:false -v:minimal -nologo
if not "%ERRORLEVEL%"=="0" (
    echo [ERROR] Build failed with exit code %ERRORLEVEL%.
    goto :fail
)

rem ---------- 4. copy to dist ----------
echo [3/3] Copying output to dist ...

set "SRCDIR=Plain Craft Launcher 2\obj\%CONFIG%"
if not exist "%SRCDIR%\Plain Craft Launcher 2.exe" set "SRCDIR=Plain Craft Launcher 2\bin"

if not exist "%SRCDIR%\Plain Craft Launcher 2.exe" (
    echo [ERROR] Build output not found: "%SRCDIR%\Plain Craft Launcher 2.exe"
    goto :fail
)

set "DSTDIR=dist\%CONFIG%"
if not exist "%DSTDIR%" mkdir "%DSTDIR%"

rem Output file name. Only the file name changes -- AssemblyName stays
rem "Plain Craft Launcher 2", so WPF pack URIs (/Assembly;component/...)
rem and XAML clr-namespace references keep resolving.
set "OUTNAME=PCL2_ACE.exe"

copy /y "%SRCDIR%\Plain Craft Launcher 2.exe" "%DSTDIR%\%OUTNAME%" >nul
if errorlevel 1 (
    echo [ERROR] Failed to copy the exe.
    goto :fail
)

rem the .exe.config is only picked up when it matches the exe name
set "SRCCFG=Plain Craft Launcher 2\bin\Plain Craft Launcher 2.exe.config"
if exist "%SRCCFG%" copy /y "%SRCCFG%" "%DSTDIR%\%OUTNAME%.config" >nul

echo.
echo ============================================================
echo   BUILD OK
echo   Output: %CD%\%DSTDIR%\%OUTNAME%
echo ============================================================
echo.

endlocal
exit /b 0

:fail
echo.
echo Build aborted.
endlocal
exit /b 1
