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
  /** cursor | openai | codex — sat af API så UI kan filtrere */
  provider?: string | null
}

export const DASHBOARD_PROVIDERS = ['cursor', 'codex', 'openai'] as const
export type DashboardProvider = (typeof DASHBOARD_PROVIDERS)[number]

export function providerLabel(p: string): string {
  switch (p.toLowerCase()) {
    case 'cursor':
      return 'Cursor'
    case 'codex':
      return 'Codex'
    case 'openai':
      return 'OpenAI'
    default:
      return p
  }
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
  backfillCodex: (body: { organizationId?: string; connectionId?: string }) =>
    apiFetch('/ingest/codex/backfill', { method: 'POST', body: JSON.stringify(body) }).then((r) =>
      json<{ upserted: number; meters: number; lifetimeTokens: number | null }>(r),
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

/** Fuldt tal med danske tusindtalsseparatorer (fx 365.994.345). */
export function formatTokens(n: number): string {
  return new Intl.NumberFormat('da-DK').format(Math.round(n))
}

/**
 * Kompakt, læsbart token-tal: 12,4k · 7,0 mio. · 1,22 mia.
 * Brug sammen med title={formatTokens(n)} til hover med præcist tal.
 */
export function formatTokensCompact(n: number): string {
  const rounded = Math.round(n)
  const abs = Math.abs(rounded)
  const sign = rounded < 0 ? '−' : ''
  const fmt = (v: number, digits: number) =>
    new Intl.NumberFormat('da-DK', {
      minimumFractionDigits: 0,
      maximumFractionDigits: digits,
    }).format(v)

  if (abs < 10_000) return sign + formatTokens(abs)
  if (abs < 1_000_000) {
    const v = abs / 1_000
    return `${sign}${fmt(v, v >= 100 ? 0 : 1)}k`
  }
  if (abs < 1_000_000_000) {
    const v = abs / 1_000_000
    return `${sign}${fmt(v, v >= 100 ? 0 : 1)} mio.`
  }
  const v = abs / 1_000_000_000
  return `${sign}${fmt(v, 2)} mia.`
}

/** Max lookback til deep backfill (2 år — providers cap'er pr. request, API chunker). */
export const MAX_HISTORY_DAYS = 730

/** Standard UI-periode for Oversigt. */
export const DEFAULT_DASHBOARD_DAYS = 30

export type RangePreset = '7d' | '30d' | 'mtd' | 'last_month' | 'ytd' | '1y' | '2y'

export const DEFAULT_RANGE_PRESET: RangePreset = '30d'

export const RANGE_PRESET_OPTIONS: { id: RangePreset; label: string }[] = [
  { id: '7d', label: '7d' },
  { id: '30d', label: '30d' },
  { id: 'mtd', label: 'MTD' },
  { id: 'last_month', label: 'Sidste md' },
  { id: 'ytd', label: 'YTD' },
  { id: '1y', label: '1 år' },
  { id: '2y', label: '2 år' },
]

/** Start af lokal kalenderdag (matcher Cursor usage-vinduer bedre end rullende timer). */
function startOfLocalDay(d: Date): Date {
  return new Date(d.getFullYear(), d.getMonth(), d.getDate(), 0, 0, 0, 0)
}

function endOfLocalDay(d: Date): Date {
  return new Date(d.getFullYear(), d.getMonth(), d.getDate(), 23, 59, 59, 999)
}

/** Inklusiv kalenderdage bagud (7d = i dag + 6 forrige dage), som Cursor Oct 09–Oct 15. */
export function rangeFromInclusiveDays(days: number): { from: string; to: string } {
  const to = new Date()
  const from = startOfLocalDay(to)
  from.setDate(from.getDate() - (days - 1))
  return { from: from.toISOString(), to: to.toISOString() }
}

export function formatRangeLabel(fromIso: string, toIso: string): string {
  const fmt = new Intl.DateTimeFormat('da-DK', { day: 'numeric', month: 'short' })
  return `${fmt.format(new Date(fromIso))} – ${fmt.format(new Date(toIso))}`
}

export function rangePresetLabel(preset: RangePreset, range?: { from: string; to: string }): string {
  const span = range ? formatRangeLabel(range.from, range.to) : null
  const base = (() => {
    switch (preset) {
      case '7d':
        return 'Seneste 7 dage'
      case '30d':
        return 'Seneste 30 dage'
      case 'mtd':
        return 'Måned til dato'
      case 'last_month':
        return 'Sidste måned'
      case 'ytd':
        return `År til dato (${new Date().getFullYear()})`
      case '1y':
        return 'Seneste år'
      case '2y':
        return 'Seneste 2 år'
    }
  })()
  return span ? `${base} · ${span}` : base
}

export function rangeFromDays(days: number): { from: string; to: string } {
  return rangeFromInclusiveDays(days)
}

export function rangeFromPreset(preset: RangePreset): { from: string; to: string } {
  const now = new Date()
  if (preset === 'ytd') {
    const from = new Date(now.getFullYear(), 0, 1, 0, 0, 0, 0)
    return { from: from.toISOString(), to: now.toISOString() }
  }
  if (preset === 'mtd') {
    const from = new Date(now.getFullYear(), now.getMonth(), 1, 0, 0, 0, 0)
    return { from: from.toISOString(), to: now.toISOString() }
  }
  if (preset === 'last_month') {
    const from = new Date(now.getFullYear(), now.getMonth() - 1, 1, 0, 0, 0, 0)
    const last = new Date(now.getFullYear(), now.getMonth(), 0)
    return { from: from.toISOString(), to: endOfLocalDay(last).toISOString() }
  }
  const days = preset === '7d' ? 7 : preset === '30d' ? 30 : preset === '1y' ? 365 : 730
  return rangeFromInclusiveDays(days)
}

/** @param days default 30 for UI; brug MAX_HISTORY_DAYS til backfill. */
export function defaultRange(days = DEFAULT_DASHBOARD_DAYS): { from: string; to: string } {
  return rangeFromDays(days)
}

export function deepBackfillFrom(): string {
  return rangeFromDays(MAX_HISTORY_DAYS).from
}
