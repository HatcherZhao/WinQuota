const PIN_KEY = 'winquota-pin'

export class PinRequiredError extends Error {
  constructor() {
    super('需要管理员 PIN')
  }
}

export function storedPin(): string | null {
  return sessionStorage.getItem(PIN_KEY)
}

export function storePin(pin: string | null) {
  if (pin) sessionStorage.setItem(PIN_KEY, pin)
  else sessionStorage.removeItem(PIN_KEY)
}

async function request(path: string, options: RequestInit = {}): Promise<any> {
  const headers: Record<string, string> = {
    'Content-Type': 'application/json',
    ...((options.headers as Record<string, string>) || {}),
  }
  const pin = storedPin()
  if (pin) headers['X-WinQuota-Pin'] = pin

  const res = await fetch(path, { ...options, headers })
  if (res.status === 403) {
    // 通知 App 弹出 PIN 输入框
    window.dispatchEvent(new CustomEvent('winquota:pin-required'))
    throw new PinRequiredError()
  }
  if (!res.ok) {
    let detail = ''
    try {
      const body = await res.json()
      detail = body?.error || ''
    } catch {
      // ignore
    }
    throw new Error(detail || `请求失败（HTTP ${res.status}）`)
  }
  const ct = res.headers.get('content-type') || ''
  return ct.includes('json') ? res.json() : null
}

export interface RuleStatus {
  id: number
  name: string
  type: 'application' | 'computer'
  enabled: boolean
  quotaSeconds: number
  bonusSeconds: number
  usedSeconds: number
  remainingSeconds: number
  running: boolean
  processes: { pid: number; name: string }[]
  iconPath: string | null
}

export interface StatusPayload {
  date: string
  computerState: string
  liveUpdateUtc: string
  rules: RuleStatus[]
}

export interface RuleDetail {
  id: number
  name: string
  type: 'application' | 'computer'
  enabled: boolean
  weekdayQuotaSeconds: number[]
  apps: {
    id: number
    applicationName: string
    processName: string
    exePath: string | null
    productName: string | null
    publisher: string | null
    signer: string | null
  }[]
}

export const api = {
  status: () => request('/api/status') as Promise<StatusPayload>,
  rules: () => request('/api/rules') as Promise<{ rules: RuleDetail[] }>,
  usage: (days: number) => request(`/api/usage?days=${days}`),
  processes: () => request('/api/processes'),
  signature: (path: string) =>
    request(`/api/signature?path=${encodeURIComponent(path)}`) as Promise<{
      trusted: boolean
      signerCn: string | null
    }>,
  addAppRule: (body: {
    name: string
    processNames: string[]
    exePath?: string
    productName?: string
    signer?: string
    minutes: number
    weekendMinutes?: number
  }) => request('/api/rules/app', { method: 'POST', body: JSON.stringify(body) }),
  addComputerRule: (body: { name: string; minutes: number; weekendMinutes?: number }) =>
    request('/api/rules/computer', { method: 'POST', body: JSON.stringify(body) }),
  updateRule: (body: { id: number; minutes: number; weekendMinutes?: number }) =>
    request('/api/rules/update', { method: 'POST', body: JSON.stringify(body) }),
  editRule: (body: {
    id: number
    name?: string
    processNames?: string[]
    exePath?: string
    productName?: string
    publisher?: string
    signer?: string
  }) => request('/api/rules/edit', { method: 'POST', body: JSON.stringify(body) }),
  enableRule: (id: number, enabled: boolean) =>
    request('/api/rules/enable', { method: 'POST', body: JSON.stringify({ id, enabled }) }),
  deleteRule: (id: number) =>
    request('/api/rules/delete', { method: 'POST', body: JSON.stringify({ id }) }),
  bonus: (id: number, minutes: number) =>
    request('/api/bonus', { method: 'POST', body: JSON.stringify({ id, minutes }) }),
  verifyPin: (pin: string) =>
    request('/api/pin/verify', { method: 'POST', body: JSON.stringify({ pin }) }) as Promise<{ ok: boolean }>,
  changePin: (newPin: string, currentPin: string | null) => {
    const headers: Record<string, string> = {}
    if (currentPin) headers['X-WinQuota-Pin'] = currentPin
    return request('/api/pin', { method: 'POST', body: JSON.stringify({ newPin }), headers })
  },
  settings: () => request('/api/settings'),
}
