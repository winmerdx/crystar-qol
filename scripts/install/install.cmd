@echo off
setlocal
title Crystar.QoL - Install

rem  ==================================================================
rem  INSTALL MODEL
rem  The release zip contains one folder named "Crystar.QoL". Extract it
rem  and move the WHOLE folder INTO the game install folder:
rem
rem     Crystar\CRYSTAR.exe          <- game files (untouched)
rem     Crystar\CRYSTAR_Data\
rem     Crystar\GameAssembly.dll
rem     Crystar\Crystar.QoL\         <- this package (the folder you moved)
rem         install.cmd              <- run this
rem         uninstall.cmd
rem         payload\                 <- framework + mod files to copy
rem
rem  install.cmd only ADDS files next to CRYSTAR.exe and records what it
rem  added in CrystarQoL_MANIFEST.txt. No game file is ever modified.
rem
rem  A plain uninstall keeps UserData\Crystar.QoL\backup\ (settings.cfg plus
rem  the save snapshots the mod took). When such a backup is found and no live
rem  settings.cfg exists, this script offers to restore the settings.
rem
rem  Usage:  install.cmd [/y]    /y = quiet (no pause), for automation
rem  ==================================================================

set "SELF=%~dp0"
for %%I in ("%SELF%.") do set "SELF=%%~fI"
set "PAYLOAD=%SELF%\payload"
set "QUIET=0"
if /i "%~1"=="/y" set "QUIET=1"

echo.
echo   Crystar.QoL installer
echo   Package folder : %SELF%

rem  ==================================================================
rem  gate 1 - package structure intact?
rem  ==================================================================
if not exist "%PAYLOAD%\" (
    echo   [ERROR] "payload" folder is missing next to this script.
    echo           The package is broken or was not extracted completely.
    echo.
    echo           Nothing was installed.
    if "%QUIET%"=="0" pause
    exit /b 1
)

rem  ==================================================================
rem  gate 2 - the package folder must live INSIDE the game folder.
rem  Walk up from here until we find a folder that looks like the Crystar
rem  game root: it must contain CRYSTAR.exe AND CRYSTAR_Data AND
rem  GameAssembly.dll. If we hit the drive root without finding it, the
rem  package is in the wrong place -> abort.
rem  ==================================================================
set "GAMEDIR="
set "CUR=%SELF%"

:walkup
set "PARENT="
for %%I in ("%CUR%\..") do set "PARENT=%%~fI"
if /i "%PARENT%"=="%CUR%" goto :walkend

if exist "%PARENT%\CRYSTAR.exe" if exist "%PARENT%\CRYSTAR_Data" if exist "%PARENT%\GameAssembly.dll" (
    set "GAMEDIR=%PARENT%"
    goto :walkend
)

set "CUR=%PARENT%"
goto :walkup
:walkend

if not defined GAMEDIR (
    echo   [ERROR] Could not find the Crystar game folder above this package.
    echo           Searched from %SELF% all the way up - no folder contains
    echo           CRYSTAR.exe + CRYSTAR_Data + GameAssembly.dll together.
    echo.
    echo           Move the whole "Crystar.QoL" folder INTO the Crystar
    echo           install folder first, then run install.cmd again.
    echo           Expected layout:
    echo             ^<Crystar folder^>\CRYSTAR.exe
    echo             ^<Crystar folder^>\CRYSTAR_Data
    echo             ^<Crystar folder^>\Crystar.QoL\install.cmd
    echo.
    echo           Nothing was installed.
    if "%QUIET%"=="0" pause
    exit /b 1
)

echo   Game folder    : %GAMEDIR%

rem  ==================================================================
rem  gate 3 - refuse to install into a CRYSTAR_Data-like subfolder
rem  ==================================================================
if /i "%GAMEDIR:~-12%"=="\CRYSTAR_Data" (
    echo   [ERROR] This looks like the CRYSTAR_Data folder.
    echo           The package must sit in the folder that contains CRYSTAR.exe.
    echo.
    echo           Nothing was installed.
    if "%QUIET%"=="0" pause
    exit /b 1
)

rem  ==================================================================
rem  gate 4 - the game must not be running
rem  ==================================================================
tasklist /fi "IMAGENAME eq CRYSTAR.exe" 2>nul | findstr /i "CRYSTAR.exe" >nul
if not errorlevel 1 (
    echo   [WARN] CRYSTAR.exe is currently running. Close the game first,
    echo          then run this again. Installing while the game runs is
    echo          not supported.
    echo.
    if "%QUIET%"=="0" pause
    exit /b 1
)

rem  ==================================================================
rem  warn if a framework install already exists (we only update files)
rem  ==================================================================
if exist "%GAMEDIR%\version.dll" (
    echo   [NOTE] Existing install detected - files will be updated.
    echo          Mods\ and UserData\ are preserved.
    echo.
)

rem  ==================================================================
rem  previous settings? a plain uninstall keeps UserData\Crystar.QoL\backup\
rem  (settings.cfg + the mod's save snapshots). Offer the settings back.
rem  ==================================================================
set "MODDIR=%GAMEDIR%\UserData\Crystar.QoL"
if exist "%MODDIR%\settings.cfg" goto :norestore
if not exist "%MODDIR%\backup\settings.cfg" goto :norestore
echo.
echo   [NOTE] Settings kept by a previous uninstall were found:
echo            %MODDIR%\backup\
set "RESTORE=y"
if "%QUIET%"=="0" set /p "RESTORE=Restore them? [Y/n] "
if /i "%RESTORE%"=="n" goto :norestore
if not exist "%MODDIR%\" mkdir "%MODDIR%" >nul 2>&1
for %%M in (settings.cfg gate.pause gate.patch overlay.off map.invert console.show) do (
    if exist "%MODDIR%\backup\%%M" copy /y "%MODDIR%\backup\%%M" "%MODDIR%\%%M" >nul 2>&1
)
echo     + settings.cfg (and marker files) restored from backup\
:norestore

set "MANIFEST=%GAMEDIR%\CrystarQoL_MANIFEST.txt"
> "%MANIFEST%" echo [F] CrystarQoL_MANIFEST.txt
>>"%MANIFEST%" echo [F] CrystarQoL_uninstall.cmd

echo   Copying files...
if exist "%PAYLOAD%\version.dll" (
    copy /y "%PAYLOAD%\version.dll" "%GAMEDIR%\" >nul
    >>"%MANIFEST%" echo [F] version.dll
    echo     + version.dll
)
if exist "%PAYLOAD%\NOTICE.txt" (
    copy /y "%PAYLOAD%\NOTICE.txt" "%GAMEDIR%\" >nul
    >>"%MANIFEST%" echo [F] NOTICE.txt
)
for %%D in (MelonLoader Mods) do (
    if exist "%PAYLOAD%\%%D" (
        xcopy /e /y /i /q "%PAYLOAD%\%%D" "%GAMEDIR%\%%D" >nul
        >>"%MANIFEST%" echo [D] %%D
        echo     + %%D\
    )
)
copy /y "%SELF%\uninstall.cmd" "%GAMEDIR%\CrystarQoL_uninstall.cmd" >nul
echo     + CrystarQoL_uninstall.cmd

echo.
echo   Installed. Start the game normally through Steam.
echo.
echo   - First launch takes longer: the mod framework generates IL2CPP
echo     proxy assemblies once.
echo   - Everything added is listed in CrystarQoL_MANIFEST.txt.
echo   - To fully undo: run CrystarQoL_uninstall.cmd in the game folder.
echo     It keeps UserData\Crystar.QoL\backup\ (your settings and the save
echo     snapshots the mod took) - running install.cmd again offers them back.
echo.
if "%QUIET%"=="0" pause
exit /b 0
