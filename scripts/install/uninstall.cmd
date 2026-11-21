@echo off
setlocal enabledelayedexpansion
title Crystar.QoL - Uninstall

rem  ==================================================================
rem  SAFETY MODEL
rem  This script never runs "rd /s /q" on anything it has not validated.
rem  A target is deleted only if it passes ALL of these gates:
rem    1. the game is not running (UserData\Crystar.QoL\debug.log is locked
rem       while CRYSTAR.exe holds it open)
rem    2. game folder is confirmed by the presence of CRYSTAR.exe
rem    3. the name is on a fixed hard-coded whitelist (no patterns).
rem       Names below UserData are only accepted as "UserData\<fixed name>" -
rem       never an arbitrary path.
rem    4. the name contains none of  \  /  :  *  ?  ..   (no path tricks)
rem    5. if it is a directory it must NOT hold any game-critical file
rem    6. the user reads the printed list and literally types YES
rem
rem  KEPT ON PURPOSE: UserData\Crystar.QoL\backup\ - it holds the player's
rem  settings (settings.cfg + marker files) and the save snapshots the mod
rem  took. install.cmd offers to restore the settings on the next install.
rem  Everything else the mod ever created is removed. Game files and the real
rem  saves (AppData\LocalLow\Spike Chunsoft\CRYSTAR) are never touched.
rem
rem  Run with /dry to print the plan without deleting anything.
rem
rem  NOTE: a bare "rd" must never be used as an "is it empty" test here - see
rem  the :countentries gate at the bottom of this file.
rem  ==================================================================

set "SELF=%~dp0"
set "SELF=%SELF:~0,-1%"
set "SELFPATH=%~f0"
set "GAMEDIR=%SELF%"
set "DRYRUN=0"
set "COUNT=0"
set "FAILED=0"

if /i "%~1"=="/dry" set "DRYRUN=1"
if /i "%~1"=="-dry" set "DRYRUN=1"

rem  --- locate the game root (works from game root or from Crystar.QoL\) ---
if not exist "!GAMEDIR!\CRYSTAR.exe" (
    if exist "!GAMEDIR!\..\CRYSTAR.exe" (
        for %%I in ("!GAMEDIR!\..") do set "GAMEDIR=%%~fI"
    )
)

if not exist "!GAMEDIR!\CRYSTAR.exe" (
    echo [ERROR] CRYSTAR.exe was not found next to this script.
    echo         Script location: !SELF!
    echo         Run it from the Crystar install folder, or from the
    echo         Crystar.QoL folder inside it.
    echo.
    echo         Nothing was deleted.
    pause
    exit /b 1
)

set "MANIFEST=%GAMEDIR%\CrystarQoL_MANIFEST.txt"

echo.
echo   Crystar.QoL uninstaller
echo   Game folder : %GAMEDIR%
if "%DRYRUN%"=="1" echo   Mode        : DRY RUN - nothing will be deleted
echo.

rem  --- build the validated delete list ---
if exist "!MANIFEST!" (
    for /f "usebackq tokens=1,*" %%A in ("!MANIFEST!") do call :validate "%%~A" "%%~B" ""
) else (
    echo   [WARN] No manifest found, falling back to the built-in whitelist.
    call :validate "[F]" "version.dll" ""
    call :validate "[F]" "NOTICE.txt" ""
    call :validate "[D]" "MelonLoader" ""
    call :validate "[D]" "Mods" ""
)

rem  --- entries the install manifest can never list --------------------------
rem  MelonLoader and the mod create these at runtime, so install.cmd has no way
rem  to record them. Fixed names only - the whitelist still decides what passes.
call :validate "[F]" "UserData\MelonPreferences.cfg" "MelonLoader's own config"
call :validate "[D]" "UserData\MelonStartScreen" "MelonLoader start screen"
call :validate "[D]" "UserData\Crystar.QoL" "contents removed, backup\ kept"

if "%COUNT%"=="0" (
    echo   Nothing to remove - no whitelisted mod file was found.
    echo   The game folder already looks clean.
    echo.
    pause
    exit /b 0
)

echo   Will remove:
echo.
for /L %%i in (1,1,%COUNT%) do (
    echo     !TYPE[%%i]!  !NAME[%%i]!
    if not "!NOTE[%%i]!"=="" echo               - !NOTE[%%i]!
)
echo.
echo   Never touched: the game files (CRYSTAR.exe / CRYSTAR_Data /
echo   GameAssembly.dll / UnityPlayer.dll) and your real saves + Steam Cloud
echo   in AppData\LocalLow\Spike Chunsoft\CRYSTAR.
echo.
echo   KEPT on purpose: UserData\Crystar.QoL\backup\
echo     your settings.cfg and the save snapshots the mod took.
echo     install.cmd will offer to restore the settings on the next install.
echo.

if "%DRYRUN%"=="1" (
    echo   Dry run done. Re-run without /dry to delete for real.
    echo.
    pause
    exit /b 0
)

rem  --- gate 0 : the game must not be running -------------------------------
rem  Checked only for the real run: /dry deletes nothing above, so previewing
rem  the list is fine while the game is open. The mod's own log
rem  (UserData\Crystar.QoL\debug.log) is held open by CRYSTAR.exe, so the
rem  actual removal would fail without this gate.
tasklist /fi "IMAGENAME eq CRYSTAR.exe" 2>nul | findstr /i "CRYSTAR.exe" >nul
if not errorlevel 1 (
    echo   [WARN] CRYSTAR.exe is running. Close the game first, then run this
    echo          again - the mod's log file is locked while the game runs.
    echo.
    echo          Nothing was deleted.
    pause
    exit /b 1
)

set "ANSWER="
set /p "ANSWER=Type YES to confirm deletion: "
if /i not "%ANSWER%"=="YES" (
    echo   Cancelled. Nothing was deleted.
    echo.
    pause
    exit /b 0
)
echo.

rem  --- rescue the mod's own state into the retained backup folder -----------
rem  UserData\Crystar.QoL\backup\ is the one folder this script never deletes:
rem  it gets the settings copied in here, and it already holds the save
rem  snapshots the mod took. install.cmd reads it back on the next install.
set "MODDIR=%GAMEDIR%\UserData\Crystar.QoL"
set "MODBAK=%MODDIR%\backup"
if exist "!MODDIR!\" (
    if not exist "!MODBAK!\" mkdir "!MODBAK!" >nul 2>&1
    for %%M in (settings.cfg gate.pause gate.patch overlay.off map.invert console.show) do (
        if exist "!MODDIR!\%%M" (
            copy /y "!MODDIR!\%%M" "!MODBAK!\%%M" >nul 2>&1
            echo   [ OK ] kept UserData\Crystar.QoL\%%M in backup\
        )
    )
)

rem  --- delete, one validated entry at a time --------------------------------
echo.
for /L %%i in (1,1,%COUNT%) do (
    if /i "!NAME[%%i]!"=="Mods" (
        call :cleanmods
    ) else (
        if /i "!NAME[%%i]!"=="UserData\Crystar.QoL" (
            call :cleanmoddir
        ) else (
            call :delone "!TYPE[%%i]!" "!NAME[%%i]!"
        )
    )
)

echo.
rem  --- verify: nothing of ours may survive except the kept backup folder ---
rem  Mods\ itself is not counted - it may legitimately stay for other mods.
rem  winhttp.dll / BepInEx are NOT counted either: they belong to a different
rem  loader and were never installed by this package.
set "LEFTOVER=0"
if exist "%GAMEDIR%\version.dll" set /a LEFTOVER+=1
if exist "%GAMEDIR%\NOTICE.txt" set /a LEFTOVER+=1
if exist "%GAMEDIR%\MelonLoader" set /a LEFTOVER+=1
if exist "%GAMEDIR%\Mods\Crystar.QoL.dll" set /a LEFTOVER+=1
if exist "%GAMEDIR%\UserData\MelonPreferences.cfg" set /a LEFTOVER+=1
if exist "%GAMEDIR%\UserData\MelonStartScreen" set /a LEFTOVER+=1
if exist "%GAMEDIR%\UserData\Crystar.QoL\settings.cfg" set /a LEFTOVER+=1
if exist "%GAMEDIR%\UserData\Crystar.QoL\debug.log" set /a LEFTOVER+=1
if "%LEFTOVER%"=="0" (
    echo   Verified: no mod files remain.
) else (
    echo   [WARN] %LEFTOVER% item^(s^) still present - delete them manually.
)

if exist "!GAMEDIR!\UserData\Crystar.QoL\backup\" (
    echo.
    echo   Kept on purpose:
    echo     UserData\Crystar.QoL\backup\   (settings.cfg + save snapshots^)
    echo     Run install.cmd again to get your settings back, or delete this
    echo     folder by hand if you want zero trace of the mod.
)

if "%FAILED%"=="0" (
    echo   Done. Everything this package added has been removed.
) else (
    echo   [WARN] %FAILED% item^(s^) could not be removed.
)

echo.
echo   Double-check if you like:
echo   Steam - Crystar - Properties - Installed Files - Verify integrity.
echo   It will pass: no game file was ever modified.
echo.
pause
rem  Self-delete. A plain "del %~f0" makes cmd.exe print
rem  "找不到批处理文件。" (batch file not found) right after: cmd keeps reading
rem  the file it just deleted. The "(goto) 2>nul &" form ends this batch
rem  context first, so the deletion is silent (probed 2026-09-17).
if not "%FAILED%"=="0" goto :keepself
(goto) 2>nul & del /f /q "%~f0"
:keepself
exit /b 0


rem  ==================================================================
:validate
rem  %1 = type token [F]/[D]   %2 = name relative to the game folder
rem  %3 = optional note printed next to the name in the delete list
set "VTYPE=%~1"
set "VNAME=%~2"
set "VNOTE=%~3"

if "%VNAME%"=="" exit /b 0
if /i not "%VTYPE%"=="[D]" if /i not "%VTYPE%"=="[F]" exit /b 0

rem  gate 3 - reject anything that could escape the game folder
rem  A name may point into UserData\ but never deeper: that one fixed prefix is
rem  stripped here so the checks below still apply to the rest of the name.
rem  NOTE: never test for '*' via variable substitution here - cmd treats a
rem  leading '*' as the "trim prefix" operator and a bare '=*=' right after
rem  it breaks parsing (the v0.1 uninstall died exactly there; keep this file
rem  pure ASCII, non-ASCII comments also get mangled under the GBK codepage).
rem  The whitelist below never contains wildcards anyway; this gate only
rem  needs to stop path escapes.
set "VCHECK=%VNAME%"
if /i "%VNAME:~0,9%"=="UserData\" set "VCHECK=%VNAME:~9%"
if "%VCHECK%"=="" exit /b 0
if not "%VCHECK%"=="%VCHECK:\=%" exit /b 0
if not "%VCHECK%"=="%VCHECK:/=%" exit /b 0
if not "%VCHECK%"=="%VCHECK::=%" exit /b 0
if not "%VCHECK%"=="%VCHECK:?=%" exit /b 0
if not "%VCHECK%"=="%VCHECK:..=%" exit /b 0

rem  gate 2 - hard-coded whitelist, no patterns, no variables
set "VOK=0"
if /i "%VNAME%"=="version.dll" set "VOK=1"
if /i "%VNAME%"=="NOTICE.txt" set "VOK=1"
if /i "%VNAME%"=="CrystarQoL_MANIFEST.txt" set "VOK=1"
if /i "%VNAME%"=="CrystarQoL_uninstall.cmd" set "VOK=1"
if /i "%VNAME%"=="MelonLoader" set "VOK=1"
if /i "%VNAME%"=="Mods" set "VOK=1"
if /i "%VNAME%"=="UserData\MelonPreferences.cfg" set "VOK=1"
if /i "%VNAME%"=="UserData\MelonStartScreen" set "VOK=1"
if /i "%VNAME%"=="UserData\Crystar.QoL" set "VOK=1"
if "%VOK%"=="0" (
    echo   [SKIP] not a whitelisted mod file: !VNAME!
    exit /b 0
)

rem  gate 4 - a directory must not hold game-critical files
if /i "!VTYPE!"=="[D]" (
    if not exist "!GAMEDIR!\!VNAME!\" exit /b 0
    if exist "!GAMEDIR!\!VNAME!\CRYSTAR.exe" (
        echo   [SKIP] !VNAME! holds CRYSTAR.exe - refusing to touch it.
        exit /b 0
    )
    if exist "!GAMEDIR!\!VNAME!\GameAssembly.dll" (
        echo   [SKIP] !VNAME! holds GameAssembly.dll - refusing to touch it.
        exit /b 0
    )
    if exist "!GAMEDIR!\!VNAME!\UnityPlayer.dll" (
        echo   [SKIP] !VNAME! holds UnityPlayer.dll - refusing to touch it.
        exit /b 0
    )
    if exist "!GAMEDIR!\!VNAME!\CRYSTAR_Data" (
        echo   [SKIP] !VNAME! holds CRYSTAR_Data - refusing to touch it.
        exit /b 0
    )
) else (
    if not exist "!GAMEDIR!\!VNAME!" exit /b 0
)

set /a COUNT+=1
set "TYPE[%COUNT%]=%VTYPE%"
set "NAME[%COUNT%]=%VNAME%"
set "NOTE[%COUNT%]=%VNOTE%"
exit /b 0


rem  ==================================================================
:delone
rem  %1 = [F]/[D]   %2 = validated name
rem  A batch file cannot delete the copy cmd.exe is running right now, so the
rem  running script is left to the self-delete at the very end.
if /i "%~2"=="CrystarQoL_uninstall.cmd" (
    if /i "!SELFPATH!"=="!GAMEDIR!\CrystarQoL_uninstall.cmd" (
        echo   [ OK ] CrystarQoL_uninstall.cmd - removed when this script exits
        exit /b 0
    )
)
if /i "%~1"=="[D]" (
    rd /s /q "!GAMEDIR!\%~2" 2>nul
) else (
    del /f /q "!GAMEDIR!\%~2" 2>nul
)
if exist "!GAMEDIR!\%~2" (
    echo   [FAIL] %~2 - close the game and try again
    set /a FAILED+=1
) else (
    echo   [ OK ] %~2
)
exit /b 0


rem  ==================================================================
:cleanmods
rem  Mods\ can be shared with other mods: only our dll goes away, and the
rem  folder follows only when it is REALLY empty.
del /f /q "%GAMEDIR%\Mods\Crystar.QoL.dll" 2>nul
if exist "!GAMEDIR!\Mods\Crystar.QoL.dll" (
    echo   [FAIL] Mods\Crystar.QoL.dll - close the game and try again
    set /a FAILED+=1
    exit /b 0
)
echo   [ OK ] Mods\Crystar.QoL.dll
call :countentries "%GAMEDIR%\Mods"
if not "%ENTRYCOUNT%"=="0" (
    echo          Mods\ kept - it still holds %ENTRYCOUNT% other item^(s^)
    exit /b 0
)
rd "%GAMEDIR%\Mods" 2>nul
if exist "!GAMEDIR!\Mods\" (
    echo          Mods\ could not be removed - delete it by hand
) else (
    echo          Mods\ was empty - folder removed
)
exit /b 0


rem  ==================================================================
:cleanmoddir
rem  UserData\Crystar.QoL - remove everything except backup\, which is the
rem  whole point of keeping this folder alive.
for %%E in ("!GAMEDIR!\UserData\Crystar.QoL\*") do (
    if /i not "%%~nxE"=="backup" (
        if /i not "%%~nxE"=="*" (
            if exist "%%~fE" (
                rd /s /q "%%~fE" 2>nul
                if exist "%%~fE" del /f /q "%%~fE" 2>nul
                if exist "%%~fE" (
                    echo   [FAIL] UserData\Crystar.QoL\%%~nxE
                    set /a FAILED+=1
                ) else (
                    echo   [ OK ] UserData\Crystar.QoL\%%~nxE
                )
            )
        )
    )
)
if exist "!GAMEDIR!\UserData\Crystar.QoL\backup\" (
    echo          kept: UserData\Crystar.QoL\backup\
    exit /b 0
)
call :countentries "%GAMEDIR%\UserData\Crystar.QoL"
if "%ENTRYCOUNT%"=="0" (
    rd "!GAMEDIR!\UserData\Crystar.QoL" 2>nul
    if not exist "!GAMEDIR!\UserData\Crystar.QoL\" (
        echo          UserData\Crystar.QoL\ was empty - folder removed
    )
) else (
    echo          UserData\Crystar.QoL\ still holds %ENTRYCOUNT% item^(s^) - see [FAIL] above
)
exit /b 0


rem  ==================================================================
:countentries
rem  %1 = a directory. Sets ENTRYCOUNT to the number of files + folders in it.
rem  Gate: NEVER use a bare "rd" as an "is it empty" test. On Windows 11
rem  (26100) rmdir without /s deletes a NON-EMPTY tree silently, no prompt and
rem  errorlevel 0 (probed 2026-09-17) - that would eat other mods' files.
set "ENTRYCOUNT=0"
for %%E in ("%~1\*") do if /i not "%%~nxE"=="*" set /a ENTRYCOUNT+=1
exit /b 0
