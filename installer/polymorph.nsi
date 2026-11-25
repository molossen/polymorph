; Polymorph installer (NSIS 3, Modern UI 2).
; makensis -DVERSION=1.0.0 -DSRC=<folder of the published app> -DOUT=<setup.exe> polymorph.nsi
; Installs for the current user (no admin rights asked): %LOCALAPPDATA%\Programs\Polymorph,
; Start menu and desktop shortcuts with the Polymorph icon, an uninstaller in "Apps & features".

Unicode true
!include "MUI2.nsh"
!include "FileFunc.nsh"

!ifndef VERSION
  !define VERSION "1.5.1"
!endif
!define APPNAME "Polymorph"
!define PUBLISHER "Molo Modding"
!define UNINSTKEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\Polymorph"

Name "${APPNAME}"
OutFile "${OUT}"
InstallDir "$LOCALAPPDATA\Programs\Polymorph"
InstallDirRegKey HKCU "Software\Polymorph" "InstallDir"
RequestExecutionLevel user
SetCompressor /SOLID lzma
BrandingText "Polymorph ${VERSION} - by Molo Modding"
VIProductVersion "${VERSION}.0"
VIAddVersionKey "ProductName" "Polymorph"
VIAddVersionKey "CompanyName" "Molo Modding"
VIAddVersionKey "FileDescription" "Polymorph installer"
VIAddVersionKey "FileVersion" "${VERSION}"
VIAddVersionKey "ProductVersion" "${VERSION}"
VIAddVersionKey "LegalCopyright" "Molo Modding"

!define MUI_ICON "${SRC}\Assets\polymorph.ico"
!define MUI_UNICON "${SRC}\Assets\polymorph.ico"
!define MUI_WELCOMEFINISHPAGE_BITMAP "welcome.bmp"
!define MUI_UNWELCOMEFINISHPAGE_BITMAP "welcome.bmp"
!define MUI_HEADERIMAGE
!define MUI_HEADERIMAGE_RIGHT
!define MUI_HEADERIMAGE_BITMAP "header.bmp"
!define MUI_ABORTWARNING
!define MUI_FINISHPAGE_RUN "$INSTDIR\Polymorph.exe"
!define MUI_FINISHPAGE_RUN_TEXT "$(RunText)"

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_LICENSE "${SRC}\LICENSE"
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_COMPONENTS
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES

!insertmacro MUI_LANGUAGE "French"
!insertmacro MUI_LANGUAGE "English"

LangString RunText ${LANG_FRENCH} "Lancer Polymorph"
LangString RunText ${LANG_ENGLISH} "Start Polymorph"
LangString SecApp ${LANG_FRENCH} "Polymorph (requis)"
LangString SecApp ${LANG_ENGLISH} "Polymorph (required)"
LangString SecDesk ${LANG_FRENCH} "Raccourci sur le bureau"
LangString SecDesk ${LANG_ENGLISH} "Desktop shortcut"
LangString Running ${LANG_FRENCH} "Polymorph est ouvert. Ferme-le, puis clique sur OK pour continuer."
LangString Running ${LANG_ENGLISH} "Polymorph is open. Close it, then click OK to go on."

Function .onInit
  !insertmacro MUI_LANGDLL_DISPLAY
FunctionEnd

; the folder always ends in "Polymorph" (the uninstaller removes that whole folder: never the Desktop or Documents)
Function .onVerifyInstDir
  StrCpy $1 "$INSTDIR" "" -9
  StrCmp $1 "Polymorph" +2
  Abort
FunctionEnd

; Polymorph must be closed: its files can't be replaced while it runs. An update started from Polymorph closes it
; right after: wait for it quietly (up to 30 s) before asking.
!macro WaitClosed
  StrCpy $2 0
  retry:
  ClearErrors
  IfFileExists "$INSTDIR\Polymorph.exe" 0 free
  FileOpen $0 "$INSTDIR\Polymorph.exe" a
  IfErrors 0 opened
  IntOp $2 $2 + 1
  IntCmp $2 60 ask 0 ask
  Sleep 500
  Goto retry
  ask:
  MessageBox MB_OKCANCEL|MB_ICONEXCLAMATION "$(Running)" IDOK retry
  Abort
  opened:
  FileClose $0
  free:
!macroend

; the MCP server (pyembed\python.exe, started by an AI app such as Claude or ChatGPT) keeps running after Polymorph is
; closed and holds the Python files: stop everything that runs from the install folder (the AI app starts it again)
!macro StopFromInstallDir
  nsExec::ExecToLog `powershell -NoProfile -ExecutionPolicy Bypass -Command "Get-Process | Where-Object { $$_.Path -and $$_.Path.StartsWith('$INSTDIR\', [StringComparison]::OrdinalIgnoreCase) } | Stop-Process -Force -ErrorAction SilentlyContinue"`
  Pop $0
  Sleep 800
!macroend

Section "$(SecApp)" SecMain
  SectionIn RO
  !insertmacro WaitClosed
  !insertmacro StopFromInstallDir
  SetOutPath "$INSTDIR"
  ; an update replaces the program files; settings, autosaves and the texture library live elsewhere and stay
  File /r "${SRC}\*.*"
  WriteUninstaller "$INSTDIR\Uninstall Polymorph.exe"
  CreateDirectory "$SMPROGRAMS\Polymorph"
  CreateShortcut "$SMPROGRAMS\Polymorph\Polymorph.lnk" "$INSTDIR\Polymorph.exe" "" "$INSTDIR\Polymorph.exe" 0
  CreateShortcut "$SMPROGRAMS\Polymorph\Uninstall Polymorph.lnk" "$INSTDIR\Uninstall Polymorph.exe"
  WriteRegStr HKCU "Software\Polymorph" "InstallDir" "$INSTDIR"
  WriteRegStr HKCU "${UNINSTKEY}" "DisplayName" "Polymorph"
  WriteRegStr HKCU "${UNINSTKEY}" "DisplayVersion" "${VERSION}"
  WriteRegStr HKCU "${UNINSTKEY}" "Publisher" "${PUBLISHER}"
  WriteRegStr HKCU "${UNINSTKEY}" "DisplayIcon" "$INSTDIR\Polymorph.exe"
  WriteRegStr HKCU "${UNINSTKEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "${UNINSTKEY}" "URLInfoAbout" "https://molossen.github.io/polymorph/"
  WriteRegStr HKCU "${UNINSTKEY}" "UninstallString" '"$INSTDIR\Uninstall Polymorph.exe"'
  WriteRegDWORD HKCU "${UNINSTKEY}" "NoModify" 1
  WriteRegDWORD HKCU "${UNINSTKEY}" "NoRepair" 1
SectionEnd

Section "$(SecDesk)" SecDesktop
  ; a silent update keeps the desktop as it is (no shortcut put back if it was removed)
  IfSilent 0 +2
  IfFileExists "$DESKTOP\Polymorph.lnk" 0 nodesk
  CreateShortcut "$DESKTOP\Polymorph.lnk" "$INSTDIR\Polymorph.exe" "" "$INSTDIR\Polymorph.exe" 0
  nodesk:
SectionEnd

; the update button in Polymorph runs the installer with /S /RELAUNCH: the new version opens by itself when done
Function .onInstSuccess
  IfSilent 0 done
  ${GetParameters} $R0
  ClearErrors
  ${GetOptions} $R0 "/RELAUNCH" $R1
  IfErrors done
  Exec '"$INSTDIR\Polymorph.exe"'
  done:
FunctionEnd

Section "Uninstall"
  !insertmacro WaitClosed
  !insertmacro StopFromInstallDir
  StrCpy $1 "$INSTDIR" "" -9
  StrCmp $1 "Polymorph" 0 +2
  RMDir /r "$INSTDIR"
  Delete "$DESKTOP\Polymorph.lnk"
  RMDir /r "$SMPROGRAMS\Polymorph"
  DeleteRegKey HKCU "${UNINSTKEY}"
  DeleteRegKey HKCU "Software\Polymorph"
SectionEnd
