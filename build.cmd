@echo off
rem SPDX-License-Identifier: MIT
rem ================================================================
rem  PCL LK build script (Plain Craft Launcher 2 -> PCLLK)
rem  Usage: double-click this file.
rem  Keep this file ASCII-only with CRLF line endings.
rem ================================================================
if /i "%~1"=="__child" goto main

rem ---- outer layer: run the work in a child shell, then ALWAYS pause
cmd /c ""%~f0" __child"
echo.
echo ================================================================
echo  build.cmd finished, exit code = %errorlevel%
echo ================================================================
pause
exit /b

:main
setlocal enabledelayedexpansion
cd /d "%~dp0"
set "LOG=%~dp0build.log"
if exist "%LOG%" del /q "%LOG%" >nul 2>nul

echo ================================================================
echo  PCL LK (PCLLK) build
echo  Folder: %cd%
echo ================================================================
echo.

rem ---------------- locate MSBuild ----------------
set "MSBUILD="
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if not exist "%VSWHERE%" set "VSWHERE=%ProgramFiles%\Microsoft Visual Studio\Installer\vswhere.exe"
if exist "%VSWHERE%" for /f "usebackq delims=" %%i in (`"%VSWHERE%" -latest -products * -requires Microsoft.Component.MSBuild -find MSBuild\**\Bin\MSBuild.exe 2^>nul`) do set "MSBUILD=%%i"
if defined MSBUILD goto msbuild_ready

echo [warn] vswhere reported nothing, scanning known install folders...
for /d %%a in ("%ProgramFiles%\Microsoft Visual Studio\*") do (
    for /d %%b in ("%%~a\*") do (
        if exist "%%~b\MSBuild\Current\Bin\MSBuild.exe" set "MSBUILD=%%~b\MSBuild\Current\Bin\MSBuild.exe"
        if exist "%%~b\MSBuild\15.0\Bin\MSBuild.exe" set "MSBUILD=%%~b\MSBuild\15.0\Bin\MSBuild.exe"
    )
)
for /d %%a in ("%ProgramFiles(x86)%\Microsoft Visual Studio\*") do (
    for /d %%b in ("%%~a\*") do (
        if exist "%%~b\MSBuild\Current\Bin\MSBuild.exe" set "MSBUILD=%%~b\MSBuild\Current\Bin\MSBuild.exe"
        if exist "%%~b\MSBuild\15.0\Bin\MSBuild.exe" set "MSBUILD=%%~b\MSBuild\15.0\Bin\MSBuild.exe"
    )
)

if not defined MSBUILD (
    echo.
    echo [ERROR] MSBuild.exe was not found.
    echo         Install Visual Studio 2022 or newer with the
    echo         ".NET desktop development" workload, then run this again.
    goto fail
)

:msbuild_ready
echo Using MSBuild: !MSBUILD!
echo.

if not exist "MeloongCore\Shared\MeloongCore.csproj" (
    echo [ERROR] MeloongCore submodule is missing.
    echo         Fix: git clone https://github.com/Meloong-Git/MeloongCore.git MeloongCore
    goto fail
)

echo [0/3] Cleaning stale caches...
if exist "Plain Craft Launcher 2\obj" rmdir /s /q "Plain Craft Launcher 2\obj"
if exist "Plain Craft Launcher 2\bin" rmdir /s /q "Plain Craft Launcher 2\bin"
if exist "PCLCS\obj" rmdir /s /q "PCLCS\obj"
echo.

echo [1/3] Building PCLCS...
call :msbuild "PCLCS\PCLCS.csproj" /t:Restore /p:NuGetAudit=false
if errorlevel 1 goto fail
call :msbuild "PCLCS\PCLCS.csproj" /p:Configuration=Release /p:NuGetAudit=false
if errorlevel 1 goto fail
echo.

echo [2/3] Building PCLLK...
call :msbuild "Plain Craft Launcher 2\Plain Craft Launcher 2.vbproj" /p:Configuration=Release /p:NuGetAudit=false
if errorlevel 1 goto fail
echo.

echo [3/3] Collecting output...
set "OUT="
if exist "Plain Craft Launcher 2\bin\Release\PCLLK.exe" set "OUT=Plain Craft Launcher 2\bin\Release"
if exist "Plain Craft Launcher 2\bin\PCLLK.exe" set "OUT=Plain Craft Launcher 2\bin"
if not defined OUT (
    echo [ERROR] PCLLK.exe was not produced. See the messages above / build.log
    goto fail
)

if not exist dist mkdir dist
del /q "dist\*.*" >nul 2>nul
if exist "dist\lib" rmdir /s /q "dist\lib"

copy /y "!OUT!\PCLLK.exe" "dist\PCLLK.exe" >nul
if errorlevel 1 (
    echo [ERROR] Could not copy PCLLK.exe from "!OUT!"
    goto fail
)
rem Single-file distribution: PCLCS / MeloongCore and runtime DLLs are
rem embedded as resources and loaded at runtime by AssemblyResolve.
if exist "Links\EasyTier.zip" (
    copy /y "Links\EasyTier.zip" "dist\EasyTier.zip" >nul
) else (
    echo [warn] Links\EasyTier.zip not found; launcher will auto-download official EasyTier on first link.
)

echo.
echo ================================================================
echo  Done:  %cd%\dist\PCLLK.exe
for %%f in ("%cd%\dist\PCLLK.exe") do echo  PCLLK.exe: %%~zf bytes
if exist "dist\EasyTier.zip" for %%f in ("dist\EasyTier.zip") do echo  EasyTier.zip: %%~zf bytes
echo ================================================================
if not defined CI start "" explorer "%cd%\dist"
endlocal
exit /b 0

rem ---------------- helpers ----------------
:msbuild
"!MSBUILD!" %* /v:m /nologo /nodeReuse:false /fl "/flp:LogFile=%LOG%;Verbosity=normal;Append"
exit /b %errorlevel%

:fail
echo.
echo ================================================================
echo  BUILD FAILED
echo  Full MSBuild output: %LOG%
if exist "%LOG%" (
    echo  ---------------- errors found in log ----------------
    findstr /i /c:"error " "%LOG%"
)
echo ================================================================
endlocal
exit /b 1
