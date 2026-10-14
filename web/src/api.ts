import { clearTokens, getAccessToken, refreshAccessToken } from './auth'

const API_BASE = import.meta.env.VITE_API_BASE_URL || '/api'

export interface Org {
  id: string
  name: string
  role: string
}

export interface Connection {
  id: string
  provider: string
  scope: string
  label: string
  isActive: boolean
  lastSyncAt: string | null
  lastSyncError: string | null
  hasCredential: boolean
}

export interface UsageSummary {
  from: string
  to: string
  costUsd: number
  chargedUsd: number
  inputTokens: number
  outputTokens: number
  cacheWriteTokens: number
  cacheReadTokens: number
  requestCount: number
  eventCount: number
}

export interface SeriesPoint {
  key: string
  day: string
  inputTokens: number
  outputTokens: number
  cacheReadTokens: number
  cacheWriteTokens: number
  costUsd: number
  requestCount: number
}

export interface UsageEvent {
  id: string
  provider: string
  occurredAt: string
  model: string | null
  userEmail: string | null
  kind: string | null
  inputTokens: number
  outputTokens: number
  cacheWriteTokens: number
  cacheReadTokens: number
  requestCount: number
  costCents: number
  chargedCents: number
  conversationId: string | null
}

export interface PagedEvents {
  items: UsageEvent[]
  total: number
  page: number
  pageSize: number
}

export interface LiveMeter {
  provider: string
  label: string
  usedPercent: number
  resetsAt: string | null
  displayValue: string | null
  measuredAt: string
}

async function apiFetch(path: string, init: RequestInit = {}, retried = false): Promise<Response> {
  const token = getAccessToken()
  const headers = new Headers(init.headers)
  if (token) headers.set('Authorization', `Bearer ${token}`)
  if (init.body && !headers.has('Content-Type')) {
    headers.set('Content-Type', 'application/json')
  }

  const res = await fetch(`${API_BASE}${path}`, { ...init, headers })

  if (res.status === 401 && !retried) {
    const next = await refreshAccessToken()
    if (next) return apiFetch(path, init, true)
    clearTokens()
    window.location.assign('/login')
  }

  return res
}

async function json<T>(res: Response): Promise<T> {
  if (!res.ok) {
    const text = await res.text()
    throw new Error(text || `HTTP ${res.status}`)
  }
  if (res.status === 204) return undefined as T
  return res.json()
}

function qs(params: Record<string, string | number | undefined | null>): string {
  const sp = new URLSearchParams()
  for (const [k, v] of Object.entries(params)) {
    if (v !== undefined && v !== null && v !== '') sp.set(k, String(v))
  }
  const s = sp.toString()
  return s ? `?${s}` : ''
}

export const api = {
  orgs: () => apiFetch('/me/orgs').then((r) => json<Org[]>(r)),
  connections: (organizationId?: string) =>
    apiFetch(`/connections${qs({ organizationId })}`).then((r) => json<Connection[]>(r)),
  createConnection: (body: {
    provider: string
    apiKey: string
    label?: string
    scope?: string
  }, organizationId?: string) =>
    apiFetch(`/connections${qs({ organizationId })}`, {
      method: 'POST',
      body: JSON.stringify(body),
    }).then((r) => json<Connection>(r)),
  deleteConnection: (id: string) =>
    apiFetch(`/connections/${id}`, { method: 'DELETE' }).then((r) => json<void>(r)),
  backfillCursor: (body: { organizationId?: string; connectionId?: string; from?: string; to?: string }) =>
    apiFetch('/ingest/cursor/backfill', { method: 'POST', body: JSON.stringify(body) }).then((r) =>
      json<{ upserted: number; rawStored: number }>(r),
    ),
  backfillOpenAi: (body: { organizationId?: string; connectionId?: string; from?: string; to?: string }) =>
    apiFetch('/ingest/openai/backfill', { method: 'POST', body: JSON.stringify(body) }).then((r) =>
      json<{ upserted: number; costRows: number }>(r),
    ),
  summary: (from: string, to: string, organizationId?: string) =>
    apiFetch(`/usage/summary${qs({ from, to, organizationId })}`).then((r) => json<UsageSummary>(r)),
  series: (from: string, to: string, groupBy: string, organizationId?: string) =>
    apiFetch(`/usage/series${qs({ from, to, groupBy, organizationId })}`).then((r) =>
      json<SeriesPoint[]>(r),
    ),
  events: (params: {
    from: string
    to: string
    page?: number
    pageSize?: number
    provider?: string
    model?: string
    q?: string
    organizationId?: string
  }) => apiFetch(`/usage/events${qs(params)}`).then((r) => json<PagedEvents>(r)),
  meters: (organizationId?: string) =>
    apiFetch(`/meters${qs({ organizationId })}`).then((r) => json<LiveMeter[]>(r)),
}

export function formatUsd(n: number): string {
  return new Intl.NumberFormat('en-US', {
    style: 'currency',
    currency: 'USD',
    maximumFractionDigits: 2,
  }).format(n)
}

export function formatTokens(n: number): string {
  return new Intl.NumberFormat('da-DK').format(n)
}

export function defaultRange(): { from: string; to: string } {
  const to = new Date()
  const from = new Date()
  from.setUTCDate(from.getUTCDate() - 30)
  return { from: from.toISOString(), to: to.toISOString() }
}
