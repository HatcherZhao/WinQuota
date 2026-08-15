; WinQuota 安装包脚本（NSIS）
; 构建：在 tools/ 目录执行 makensis installer.nsi（需先发布 publish/service 与 publish/tray）
; 版本号在此处与 README 同步修改

!include "MUI2.nsh"
!include "LogicLib.nsh"

!define VERSION "0.8.0"
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
VIProductVersion "0.8.0.0"

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
  ; 残留服务进程未退净会留下“标记删除”，导致重建失败（1072）
  nsExec::ExecToLog 'taskkill.exe /IM WinQuota.Service.exe /F'
  Sleep 1500

  SetOutPath "$INSTDIR"

  File "..\publish\service\WinQuota.Service.exe"
  File /r "..\publish\service\wwwroot"
  File "..\publish\tray\WinQuota.Tray.exe"
  File /nonfatal "使用说明.txt"

  DetailPrint "正在安装 Windows 服务..."
  ; 升级优先原位更新（sc config），避免反复 create/delete 触发安全软件启发式；
  ; 首次安装（服务不存在，config 返回 1060）才走创建路径
  nsExec::ExecToLog 'sc.exe config WinQuota binPath= $\"$INSTDIR\WinQuota.Service.exe$\" start= auto obj= LocalSystem'
  Pop $R0
  ${If} $R0 != 0
    nsExec::ExecToLog 'sc.exe delete WinQuota'
    Sleep 1500
    nsExec::ExecToLog 'sc.exe create WinQuota binPath= $\"$INSTDIR\WinQuota.Service.exe$\" start= auto obj= LocalSystem DisplayName= $\"WinQuota 防沉迷服务$\"'
    ; 部分环境下 sc.exe create 会被安全软件干扰失败（RPC 1783），
    ; 检测失败则回退 PowerShell New-Service（直接调用 CreateServiceW API）
    nsExec::ExecToLog 'sc.exe query WinQuota'
    Pop $R0
    ${If} $R0 != 0
      DetailPrint "sc.exe create 失败（$R0），回退 New-Service..."
      nsExec::ExecToLog "powershell.exe -NoProfile -ExecutionPolicy Bypass -Command $\"try { New-Service -Name WinQuota -DisplayName 'WinQuota 防沉迷服务' -Description 'WinQuota 防沉迷：停止或删除本服务将导致限制失效。' -BinaryPathName '$INSTDIR\WinQuota.Service.exe' -StartupType Automatic } catch { $$_.Exception.Message | Out-File -Encoding utf8 '$INSTDIR\install-fallback-error.log' }$\""
    ${EndIf}
  ${EndIf}
  nsExec::ExecToLog 'sc.exe description WinQuota $\"WinQuota 防沉迷：进程监控、每日额度、时间限制。停止或删除本服务将导致限制失效。$\"'
  ; 故障自恢复：异常退出/被强杀后由 SCM 自动重启
  nsExec::ExecToLog 'sc.exe failure WinQuota reset= 86400 actions= restart/60000/restart/60000/restart/60000'
  ; 服务 ACL 加固：DACL 保护（P），仅 SYSTEM 与管理员可启停/配置服务
  nsExec::ExecToLog 'sc.exe sdset WinQuota $\"D:P(A;;GA;;;SY)(A;;GA;;;BA)$\"'
  ; 数据目录 ACL 加固（按 SID 指定，不受系统语言影响）：数据库与完整性密钥仅 SYSTEM/管理员可访问
  ExpandEnvStrings $0 "%ProgramData%"
  CreateDirectory "$0\WinQuota"
  nsExec::ExecToLog 'icacls.exe "$0\WinQuota" /inheritance:r /grant:r *S-1-5-18:(OI)(CI)F /grant:r *S-1-5-32-544:(OI)(CI)F'
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
