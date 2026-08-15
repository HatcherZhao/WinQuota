# CLI 参考

服务可执行文件带参数运行即进入 CLI 管理模式（不影响正在运行的后台服务）。

```text
winquota rules add --name 野狐围棋 --process foxwq.exe --process foxwqclient.exe --minutes 120 --weekend-minutes 240
winquota rules add-computer --name 电脑使用 --minutes 120 --weekend-minutes 240
winquota rules list
winquota rules enable --id 1 | rules disable --id 1
winquota rules remove --id 1
winquota usage [--date yyyy-MM-dd] [--days n]
winquota bonus --id 1 --minutes 15      # 当天临时奖励（应用与整机规则通用）
winquota pin set | pin verify --value <PIN> | pin has
winquota debug scan                     # 诊断：扫描进程并显示各规则匹配结果
winquota debug session                  # 诊断：当前会话状态（锁屏/空闲判定原始数据）
winquota debug lock                     # 诊断：立即锁定当前会话
winquota debug integrity                # 诊断：数据库完整性校验（直改/回滚/密钥状态）
winquota debug signature <exe路径>      # 诊断：验证 exe 数字签名并显示签名者 CN
```

`rules add` 可选参数：

- `--path <exe完整路径>`：配置后按路径精确匹配（防止改名/复制绕过），未配置时按进程名匹配（不区分大小写）
- `--signer <签名者CN>`：配置后任何由该签名者有效签名且未篡改的 exe 都会命中（最强识别方式，先用 `debug signature <exe路径>` 查询实际签名者）
