; WinQuota 安装包脚本（NSIS）
; 构建：在 tools/ 目录执行 makensis installer.nsi（需先发布 publish/service 与 publish/tray）
; 版本号在此处与 README 同步修改

!include "MUI2.nsh"

!define VERSION "0.5.0"
!define UNINST_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\WinQuota"

Name "WinQuota 防沉迷 ${VERSION}"
OutFile "..\dist\WinQuota-Setup-${VERSION}.exe"
InstallDir "$PROGRAMFILES64\WinQuota"
InstallDirRegKey HKLM "Software\WinQuota" "InstallDir"
RequestExecutionLevel admin
Unicode true
ShowInstDetails show
ShowUnInstDetails show
SetCompressor /SOLID lzma

VIAddVersionKey "ProductName" "WinQuota"
VIAddVersionKey "FileDescription" "WinQuota 防沉迷安装程序"
VIAddVersionKey "FileVersion" "${VERSION}"
VIAddVersionKey "LegalCopyright" "WinQuota"
VIProductVersion "0.5.0.0"

!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!define MUI_FINISHPAGE_RUN "$INSTDIR\WinQuota.Tray.exe"
!define MUI_FINISHPAGE_RUN_TEXT "立即运行 WinQuota 托盘"
!define MUI_FINISHPAGE_SHOWREADME_NOTCHECKED
!define MUI_FINISHPAGE_SHOWREADME "$INSTDIR\使用说明.txt"
!define MUI_FINISHPAGE_SHOWREADME_TEXT "查看使用说明"
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES

!insertmacro MUI_LANGUAGE "SimpChinese"

; ---------- 安装 ----------

Section "WinQuota 核心（后台服务 + 管理界面 + 托盘）" SecCore
  SectionIn RO
  ; 覆盖安装时先结束托盘，避免文件被占用
  nsExec::ExecToLog 'taskkill.exe /IM WinQuota.Tray.exe /F'
  Sleep 1000

  DetailPrint "正在停止旧服务..."
  ; 必须先停服务释放文件占用，再复制新文件
  nsExec::ExecToLog 'sc.exe stop WinQuota'
  Sleep 2000
  nsExec::ExecToLog 'sc.exe delete WinQuota'
  Sleep 1500

  SetOutPath "$INSTDIR"

  File "..\publish\service\WinQuota.Service.exe"
  File /r "..\publish\service\wwwroot"
  File "..\publish\tray\WinQuota.Tray.exe"
  File /nonfatal "使用说明.txt"

  DetailPrint "正在安装 Windows 服务..."

  nsExec::ExecToLog 'sc.exe create WinQuota binPath= $\"$INSTDIR\WinQuota.Service.exe$\" start= auto obj= LocalSystem DisplayName= $\"WinQuota 防沉迷服务$\"'
  nsExec::ExecToLog 'sc.exe description WinQuota $\"WinQuota 防沉迷：进程监控、每日额度、时间限制。停止或删除本服务将导致限制失效。$\"'
  ; 故障自恢复：异常退出/被强杀后由 SCM 自动重启
  nsExec::ExecToLog 'sc.exe failure WinQuota reset= 86400 actions= restart/60000/restart/60000/restart/60000'
  nsExec::ExecToLog 'sc.exe start WinQuota'

  WriteRegStr HKLM "Software\WinQuota" "InstallDir" "$INSTDIR"

  ; 控制面板“应用与功能”卸载入口
  WriteRegStr HKLM "${UNINST_KEY}" "DisplayName" "WinQuota 防沉迷"
  WriteRegStr HKLM "${UNINST_KEY}" "DisplayVersion" "${VERSION}"
  WriteRegStr HKLM "${UNINST_KEY}" "Publisher" "WinQuota"
  WriteRegStr HKLM "${UNINST_KEY}" "UninstallString" "$\"$INSTDIR\Uninstall.exe$\""
  WriteRegDWORD HKLM "${UNINST_KEY}" "NoModify" 1
  WriteRegDWORD HKLM "${UNINST_KEY}" "NoRepair" 1

  WriteUninstaller "$INSTDIR\Uninstall.exe"

  ; 开始菜单
  CreateDirectory "$SMPROGRAMS\WinQuota"
  WriteINIStr "$SMPROGRAMS\WinQuota\管理界面.url" "InternetShortcut" "URL" "http://127.0.0.1:58390/"
  CreateShortCut "$SMPROGRAMS\WinQuota\WinQuota 托盘.lnk" "$INSTDIR\WinQuota.Tray.exe"
  CreateShortCut "$SMPROGRAMS\WinQuota\卸载 WinQuota.lnk" "$INSTDIR\Uninstall.exe"
SectionEnd

Section /o "开机自动启动托盘（当前用户）" SecAutoStart
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "WinQuota Tray" "$\"$INSTDIR\WinQuota.Tray.exe$\""
SectionEnd

LangString DESC_SecCore ${LANG_SIMPCHINESE} "后台服务（进程监控与额度限制）、网页管理界面、系统托盘。安装后服务自动启动。"
LangString DESC_SecAutoStart ${LANG_SIMPCHINESE} "当前 Windows 用户登录后自动运行托盘程序。"
!insertmacro MUI_FUNCTION_DESCRIPTION_BEGIN
  !insertmacro MUI_DESCRIPTION_TEXT ${SecCore} $(DESC_SecCore)
  !insertmacro MUI_DESCRIPTION_TEXT ${SecAutoStart} $(DESC_SecAutoStart)
!insertmacro MUI_FUNCTION_DESCRIPTION_END

; ---------- 卸载 ----------

Section "Uninstall"
  ; 停止并移除托盘与服务
  nsExec::ExecToLog 'taskkill.exe /IM WinQuota.Tray.exe /F'
  nsExec::ExecToLog 'sc.exe stop WinQuota'
  Sleep 2000
  nsExec::ExecToLog 'sc.exe delete WinQuota'
  Sleep 1000

  DeleteRegValue HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "WinQuota Tray"
  DeleteRegKey HKLM "Software\WinQuota"
  DeleteRegKey HKLM "${UNINST_KEY}"

  Delete "$SMPROGRAMS\WinQuota\管理界面.url"
  Delete "$SMPROGRAMS\WinQuota\WinQuota 托盘.lnk"
  Delete "$SMPROGRAMS\WinQuota\卸载 WinQuota.lnk"
  RMDir "$SMPROGRAMS\WinQuota"

  Delete "$INSTDIR\WinQuota.Service.exe"
  Delete "$INSTDIR\WinQuota.Tray.exe"
  Delete "$INSTDIR\Uninstall.exe"
  Delete "$INSTDIR\使用说明.txt"
  RMDir /r "$INSTDIR\wwwroot"
  RMDir "$INSTDIR"

  ; 静默卸载时不弹框；数据与日志有意保留
  MessageBox MB_OK|MB_ICONINFORMATION "卸载完成。使用数据与日志保留在 $\r$\n%ProgramData%\WinQuota$\r$\n如需彻底删除请手动移除该目录。" /SD IDOK
SectionEnd
