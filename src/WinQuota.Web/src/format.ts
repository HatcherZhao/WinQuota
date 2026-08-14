export function fmtDuration(seconds: number): string {
  const s = Math.max(0, Math.floor(seconds))
  const h = Math.floor(s / 3600)
  const m = Math.floor((s % 3600) / 60)
  const sec = s % 60
  const parts: string[] = []
  if (h > 0) parts.push(`${h}小时`)
  if (m > 0) parts.push(`${m}分`)
  if (sec > 0 || parts.length === 0) parts.push(`${sec}秒`)
  return parts.join('')
}

export function fmtMinutes(seconds: number): string {
  const m = Math.round(Math.max(0, seconds) / 60)
  if (m < 60) return `${m}分钟`
  return `${Math.floor(m / 60)}小时${m % 60 > 0 ? `${m % 60}分` : ''}`
}

export const computerStateText: Record<string, string> = {
  active: '正在使用',
  locked: '已锁屏（不计入）',
  idle: '空闲（不计入）',
  nousersession: '无登录会话',
}
