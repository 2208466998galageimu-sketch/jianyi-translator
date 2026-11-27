; Isolated previous-version fixture. Never uses the real product registry or process name.
Unicode true
!include "LogicLib.nsh"
Name "Jianyi upgrade fixture"
OutFile "${OutputFile}"
RequestExecutionLevel user
SilentInstall silent
SilentUnInstall silent
InstallDir "${FixtureRoot}"
Section
  SetOutPath "$INSTDIR"
  File /r "${SourceDir}\*.*"
  WriteUninstaller "$INSTDIR\uninstall.exe"
  WriteRegStr HKCU "${AppKey}" "InstallDir" "$INSTDIR"
  WriteRegStr HKCU "${UninstallKey}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "${UninstallKey}" "DisplayVersion" "0.6.7.0"
SectionEnd
Section "Uninstall"
!ifdef FailUninstall
  SetErrorLevel 5
  Quit
!else ifdef LeaveProgram
  SetErrorLevel 0
  Quit
!else
  Sleep 350
  RMDir /r "$INSTDIR\assets"
  RMDir /r "$INSTDIR\LocalMtHost"
  RMDir /r "$INSTDIR\OcrHost"
  Delete "$INSTDIR\*.*"
  DeleteRegKey HKCU "${AppKey}"
  DeleteRegKey HKCU "${UninstallKey}"
  FileOpen $0 "$INSTDIR\data\uninstalled.txt" w
  FileWrite $0 "finished"
  FileClose $0
!endif
SectionEnd
