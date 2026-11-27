; 简译（Jianyi）安装包 —— NSIS 3（复用 Tauri 自带的 NSIS，无需另外安装）
; 构建：makensis /DAppVersion=0.5.0.0 /DSourceDir=... /DOutputFile=... installer\Jianyi.nsi
;
; 设计：
; - 按用户安装到 %LOCALAPPDATA%\Programs\Jianyi，不需要管理员权限、不触发 UAC；
; - 安装目录用户可写，AppPaths 的「exe 旁 data\」便携模式继续有效；
; - 卸载默认保留 data\（设置、日志、本地 MT 模型）；
; - 不签名：用户第一次运行安装包时 SmartScreen 提示一次，安装后启动不再提示。

Unicode true

!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "FileFunc.nsh"
!include "Sections.nsh"
!include "nsDialogs.nsh"

Var PreviousInstallDir
Var PreviousVersion
Var OcrSelected
Var WelcomeText
Var CleanInstall
Var CleanCheck

; 安装程序 exe 与向导界面统一使用应用图标
Icon "..\src\Thanslator.App\Assets\app.ico"

; 由 tools\publish.ps1 传入；直接手动编译时用下面的默认值
!ifndef AppVersion
  !define AppVersion "0.5.0.0"
!endif
!ifndef SourceDir
  !define SourceDir "..\dist\M5-folder"
!endif
!ifndef OutputFile
  !define OutputFile "..\dist\Jianyi-Setup-${AppVersion}.exe"
!endif

!define PRODUCT_NAME "简译"
!define PRODUCT_PUBLISHER "Jianyi"
!define PRODUCT_EXE "Thanslator.App.exe"
!define UNINST_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\Jianyi"
!define UNINSTALL_EXE "Jianyi-uninstall.exe"
!ifndef InstallScriptDir
  !define InstallScriptDir "."
!endif
!ifndef LegacyManifestDir
  !define LegacyManifestDir "legacy"
!endif
!tempfile MainFileManifest
!system 'powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "${InstallScriptDir}\InstallFiles.ps1" -Mode Build -Source "${SourceDir}" -Output "${MainFileManifest}"' = 0
!ifdef OcrSourceDir
  !tempfile OcrFileManifest
  !system 'powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "${InstallScriptDir}\InstallFiles.ps1" -Mode Build -Source "${OcrSourceDir}" -Output "${OcrFileManifest}" -Prefix OcrHost' = 0
!endif

Name "${PRODUCT_NAME}"
OutFile "${OutputFile}"
InstallDir "$LOCALAPPDATA\Programs\Jianyi"
InstallDirRegKey HKCU "Software\Jianyi" "InstallDir"
RequestExecutionLevel user
SetCompressor /SOLID lzma
SetCompressorDictSize 64
BrandingText "${PRODUCT_NAME} ${AppVersion}"

VIProductVersion "${AppVersion}"
VIAddVersionKey "ProductName" "${PRODUCT_NAME}"
VIAddVersionKey "CompanyName" "${PRODUCT_PUBLISHER}"
VIAddVersionKey "FileDescription" "${PRODUCT_NAME} 安装程序"
VIAddVersionKey "FileVersion" "${AppVersion}"

!define MUI_ABORTWARNING
!define MUI_ICON "..\src\Thanslator.App\Assets\app.ico"
!define MUI_UNICON "..\src\Thanslator.App\Assets\app.ico"
!define MUI_FINISHPAGE_RUN "$INSTDIR\${PRODUCT_EXE}"
!define MUI_FINISHPAGE_RUN_TEXT "启动 ${PRODUCT_NAME}"
!define MUI_WELCOMEPAGE_TEXT "$WelcomeText"
!insertmacro MUI_PAGE_WELCOME
!define MUI_PAGE_CUSTOMFUNCTION_PRE UpgradeDirectoryPre
!insertmacro MUI_PAGE_DIRECTORY
Page custom ExistingInstallPage ExistingInstallLeave
!insertmacro MUI_PAGE_COMPONENTS
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES

; 第一个语言是默认语言
!insertmacro MUI_LANGUAGE "SimpChinese"
!insertmacro MUI_LANGUAGE "English"

Function .onInit
  SetShellVarContext current
  StrCpy $WelcomeText "本向导将安装 ${PRODUCT_NAME} ${AppVersion}。$\r$\n$\r$\n点击「安装」后会关闭正在运行的简译及其组件。"
  ReadRegStr $PreviousInstallDir HKCU "Software\Jianyi" "InstallDir"
  ${If} $PreviousInstallDir == ""
    ReadRegStr $PreviousInstallDir HKCU "${UNINST_KEY}" "InstallLocation"
  ${EndIf}
  ${If} $PreviousInstallDir != ""
    GetFullPathName $PreviousInstallDir "$PreviousInstallDir"
    IfFileExists "$PreviousInstallDir\${PRODUCT_EXE}" previousFound
    IfFileExists "$PreviousInstallDir\${UNINSTALL_EXE}" previousFound
    IfFileExists "$PreviousInstallDir\uninstall.exe" previousFound
    StrCpy $PreviousInstallDir ""
  ${EndIf}
  Return
  previousFound:
  StrCpy $INSTDIR $PreviousInstallDir
  ReadRegStr $PreviousVersion HKCU "${UNINST_KEY}" "DisplayVersion"
  StrCpy $WelcomeText "检测到已安装版本 $PreviousVersion：$\r$\n$PreviousInstallDir$\r$\n$\r$\n点击「安装」后会关闭简译，按版本清单清理旧程序文件，再在原目录安装 ${AppVersion}。默认保留本地数据。"
  Call ConfigurePreviousOcr
FunctionEnd

Function UpgradeDirectoryPre
  ${If} $PreviousInstallDir != ""
    StrCpy $INSTDIR $PreviousInstallDir
    Abort
  ${EndIf}
FunctionEnd

Function ExistingInstallPage
  StrCpy $CleanInstall 0
  StrCpy $1 "未检测到"
  StrCpy $2 "未检测到"
  IfFileExists "$INSTDIR\${PRODUCT_EXE}" programFound
  IfFileExists "$INSTDIR\${UNINSTALL_EXE}" programFound
  IfFileExists "$INSTDIR\uninstall.exe" 0 checkData
  programFound:
  StrCpy $1 "已检测到"
  checkData:
  IfFileExists "$INSTDIR\data\." 0 showPage
  StrCpy $2 "已检测到"
  showPage:
  ${If} $1 == "未检测到"
  ${AndIf} $2 == "未检测到"
    Abort
  ${EndIf}
  !insertmacro MUI_HEADER_TEXT "现有安装" "选择保留数据或清洁安装"
  nsDialogs::Create 1018
  Pop $0
  ${If} $0 == error
    Abort
  ${EndIf}
  ${NSD_CreateLabel} 0 0 100% 42u "目录：$INSTDIR$\r$\n旧程序：$1$\r$\n本地数据：$2"
  Pop $0
  ${NSD_CreateCheckbox} 0 48u 100% 16u "清洁安装（卸载旧程序并删除本地数据）"
  Pop $CleanCheck
  ${If} $2 == "未检测到"
    EnableWindow $CleanCheck 0
  ${ElseIf} $PreviousInstallDir == ""
    IfFileExists "$INSTDIR\data\thanslator.db" +2
    EnableWindow $CleanCheck 0
  ${EndIf}
  nsDialogs::Show
FunctionEnd

Function ExistingInstallLeave
  ${NSD_GetState} $CleanCheck $0
  StrCpy $CleanInstall 0
  ${If} $0 == ${BST_CHECKED}
    MessageBox MB_YESNO|MB_ICONEXCLAMATION|MB_DEFBUTTON2 "重新安装时将永久删除以下目录内的设置、知识库、日志和模型：$\r$\n$INSTDIR\data$\r$\n$\r$\n确定清洁安装？" /SD IDNO IDYES confirmed
    Abort
    confirmed:
    StrCpy $CleanInstall 1
  ${EndIf}
FunctionEnd

; The same path checks run before uninstall and again before deleting any data.
Function ProcessCleanData
  Exch $0
  Push $1
  InitPluginsDir
  File /oname=$PLUGINSDIR\CleanInstallData.ps1 "CleanInstallData.ps1"
  System::Call 'kernel32::SetEnvironmentVariableW(w "JianyiCleanInstallDir", w "$INSTDIR") i.r1'
  ${If} $1 != 0
    nsExec::ExecToStack '"$SYSDIR\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "$PLUGINSDIR\CleanInstallData.ps1" $0'
    Pop $1
    Pop $0
    System::Call 'kernel32::SetEnvironmentVariableW(w "JianyiCleanInstallDir", p 0)'
  ${Else}
    StrCpy $1 "error"
  ${EndIf}
  ${If} $1 != 0
    MessageBox MB_OK|MB_ICONSTOP "无法安全清理 $INSTDIR\data。请检查文件占用、权限或目录链接。" /SD IDOK
    SetErrorLevel 2
    Abort
  ${EndIf}
  Pop $1
  Pop $0
FunctionEnd

!macro ProgramFileFunction PREFIX
Function ${PREFIX}ProcessProgramFiles
  Pop $0
  InitPluginsDir
  ReadRegStr $3 HKCU "${UNINST_KEY}" "ProgramManifestHash"
  ReadRegStr $4 HKCU "${UNINST_KEY}" "InstallLocation"
  ${If} $4 != $INSTDIR
    StrCpy $3 ""
  ${EndIf}
  System::Call 'kernel32::SetEnvironmentVariableW(w "JianyiProgramDir", w "$INSTDIR")'
  System::Call 'kernel32::SetEnvironmentVariableW(w "JianyiProgramName", w "${PRODUCT_EXE}")'
  System::Call 'kernel32::SetEnvironmentVariableW(w "JianyiManifestHash", w "$3")'
  StrCpy $3 "0"
  ${If} $PreviousInstallDir != ""
    StrCpy $3 "1"
  ${EndIf}
  System::Call 'kernel32::SetEnvironmentVariableW(w "JianyiUpgrading", w "$3")'
  System::Call 'kernel32::SetEnvironmentVariableW(w "JianyiPreviousVersion", w "$PreviousVersion")'
  System::Call 'kernel32::SetEnvironmentVariableW(w "JianyiInstallOcr", w "$OcrSelected")'
  SetOutPath "$PLUGINSDIR"
  File "${InstallScriptDir}\InstallFiles.ps1"
  !if "${PREFIX}" == ""
    File /oname=program-main.lst "${MainFileManifest}"
    !ifdef OcrSourceDir
      File /oname=program-ocr.lst "${OcrFileManifest}"
    !endif
    SetOutPath "$PLUGINSDIR\Legacy"
    File "${LegacyManifestDir}\*.lst"
  !endif
  nsExec::ExecToStack '"$SYSDIR\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "$PLUGINSDIR\InstallFiles.ps1" -Mode $0'
  Pop $1
  Pop $2
  ${If} $1 != 0
    DetailPrint "$2"
    MessageBox MB_OK|MB_ICONSTOP "无法确认或处理安装目录中的程序文件。请检查占用、旧版版本或改用空目录；未知文件会保留。" /SD IDOK
    SetErrorLevel 2
    Abort
  ${EndIf}
  !if "${PREFIX}" == ""
    ${If} $0 == "Register"
      WriteRegStr HKCU "${UNINST_KEY}" "ProgramManifestHash" "$2"
    ${EndIf}
  !endif
FunctionEnd
!macroend

!insertmacro ProgramFileFunction ""
!insertmacro ProgramFileFunction "un."

Function un.onInit
  SetShellVarContext current
FunctionEnd

Section "简译主程序" SecMain
  SectionIn RO
  ${If} $PreviousInstallDir != ""
    StrCpy $INSTDIR $PreviousInstallDir
  ${EndIf}
  Call ReadOcrSelection
  Push "Check"
  Call ProcessProgramFiles
  ${If} $CleanInstall == 1
    Push ""
    Call ProcessCleanData
  ${EndIf}
  nsExec::ExecToStack 'taskkill /IM ${PRODUCT_EXE} /T /F'
  Pop $0
  Pop $1
  Push "Remove"
  Call ProcessProgramFiles
  ${If} $CleanInstall == 1
    Push "-Delete"
    Call ProcessCleanData
  ${EndIf}
  SetOutPath "$INSTDIR"
  File /r "${SourceDir}\*.*"
  WriteUninstaller "$INSTDIR\${UNINSTALL_EXE}"
SectionEnd

Section "桌面快捷方式" SecDesktop
  CreateShortcut "$DESKTOP\简译.lnk" "$INSTDIR\${PRODUCT_EXE}"
SectionEnd

!ifdef OcrSourceDir
Section /o "本地屏幕 OCR（可选，离线识别）" SecOcr
  SetOutPath "$INSTDIR\OcrHost"
  File /r "${OcrSourceDir}\*.*"
SectionEnd
!endif

Section -Finalize
  Push "Register"
  Call ProcessProgramFiles
  SetOutPath "$INSTDIR"
  CreateDirectory "$SMPROGRAMS\简译"
  CreateShortcut "$SMPROGRAMS\简译\简译.lnk" "$INSTDIR\${PRODUCT_EXE}"
  CreateShortcut "$SMPROGRAMS\简译\卸载 简译.lnk" "$INSTDIR\${UNINSTALL_EXE}"

  WriteRegStr HKCU "Software\Jianyi" "InstallDir" "$INSTDIR"
  WriteRegStr HKCU "${UNINST_KEY}" "DisplayName" "${PRODUCT_NAME}"
  WriteRegStr HKCU "${UNINST_KEY}" "DisplayVersion" "${AppVersion}"
  WriteRegStr HKCU "${UNINST_KEY}" "Publisher" "${PRODUCT_PUBLISHER}"
  WriteRegStr HKCU "${UNINST_KEY}" "DisplayIcon" "$INSTDIR\${PRODUCT_EXE}"
  WriteRegStr HKCU "${UNINST_KEY}" "UninstallString" '"$INSTDIR\${UNINSTALL_EXE}"'
  WriteRegStr HKCU "${UNINST_KEY}" "QuietUninstallString" '"$INSTDIR\${UNINSTALL_EXE}" /S'
  WriteRegStr HKCU "${UNINST_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegDWORD HKCU "${UNINST_KEY}" "NoModify" 1
  WriteRegDWORD HKCU "${UNINST_KEY}" "NoRepair" 1
SectionEnd

Function ReadOcrSelection
  StrCpy $OcrSelected "0"
  !ifdef OcrSourceDir
    SectionGetFlags ${SecOcr} $0
    IntOp $0 $0 & ${SF_SELECTED}
    ${If} $0 != 0
      StrCpy $OcrSelected "1"
    ${EndIf}
  !endif
FunctionEnd

Function ConfigurePreviousOcr
  IfFileExists "$PreviousInstallDir\OcrHost\Jianyi.OcrHost.exe" 0 ocrDone
!ifdef OcrSourceDir
  SectionGetFlags ${SecOcr} $0
  IntOp $0 $0 | ${SF_SELECTED}
  IntOp $0 $0 | ${SF_RO}
  SectionSetFlags ${SecOcr} $0
!else
  MessageBox MB_OK|MB_ICONSTOP "旧版已安装本地 OCR，请使用带 OCR 组件的安装包升级，以保留该功能。尚未卸载或修改旧版。" /SD IDOK
  SetErrorLevel 2
  Abort
!endif
  ocrDone:
FunctionEnd

Section "Uninstall"
  ; 正在运行的实例会占用文件，先结束
  nsExec::ExecToStack 'taskkill /IM ${PRODUCT_EXE} /T /F'
  Pop $0
  Pop $1
  Push "Uninstall"
  Call un.ProcessProgramFiles
  Delete "$DESKTOP\简译.lnk"
  Delete "$SMPROGRAMS\简译\简译.lnk"
  Delete "$SMPROGRAMS\简译\卸载 简译.lnk"
  RMDir "$SMPROGRAMS\简译"
  RMDir "$INSTDIR"
  DeleteRegKey HKCU "${UNINST_KEY}"
  DeleteRegKey HKCU "Software\Jianyi"
SectionEnd

!delfile "${MainFileManifest}"
!ifdef OcrSourceDir
  !delfile "${OcrFileManifest}"
!endif
