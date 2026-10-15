import { useCallback, useEffect, useMemo, useState } from 'react'
import { DailyUsageChart, type DailyPoint } from '../components/DailyUsageChart'
import { KpiHero } from '../components/KpiHero'
import { MeterPanel } from '../components/MeterPanel'
import { ModelLeaderboard, type ModelStat } from '../components/ModelLeaderboard'
import { ProviderBreakdown, type ProviderStat } from '../components/ProviderBreakdown'
import { ProviderToggles } from '../components/ProviderToggles'
import { RangeChips } from '../components/RangeChips'
import {
  api,
  DASHBOARD_PROVIDERS,
  DEFAULT_RANGE_PRESET,
  rangeFromPreset,
  rangePresetLabel,
  type DashboardProvider,
  type LiveMeter,
  type RangePreset,
  type SeriesPoint,
  type UsageSummary,
} from '../api'

function eachUtcDay(fromIso: string, toIso: string): string[] {
  const out: string[] = []
  const cur = new Date(fromIso)
  cur.setUTCHours(0, 0, 0, 0)
  const end = new Date(toIso)
  end.setUTCHours(0, 0, 0, 0)
  while (cur <= end) {
    out.push(cur.toISOString().slice(0, 10))
    cur.setUTCDate(cur.getUTCDate() + 1)
  }
  return out
}

function pointProvider(p: SeriesPoint): string {
  return (p.provider || p.key || '').toLowerCase()
}

function filterSeries(series: SeriesPoint[], enabled: Set<string>): SeriesPoint[] {
  return series.filter((p) => {
    const prov = pointProvider(p)
    if (!prov) return true
    return enabled.has(prov)
  })
}

function aggregateModels(series: SeriesPoint[]): ModelStat[] {
  const map = new Map<string, ModelStat>()
  for (const p of series) {
    const cur = map.get(p.key) ?? { model: p.key, input: 0, output: 0, cost: 0, requests: 0 }
    cur.input += p.inputTokens
    cur.output += p.outputTokens
    cur.cost += p.costUsd
    cur.requests += p.requestCount
    map.set(p.key, cur)
  }
  return [...map.values()]
}

function aggregateProviders(series: SeriesPoint[]): ProviderStat[] {
  const map = new Map<string, ProviderStat>()
  for (const p of series) {
    const key = (p.provider || p.key || 'unknown').toLowerCase()
    const cur = map.get(key) ?? { provider: key, input: 0, output: 0, cost: 0, requests: 0 }
    cur.input += p.inputTokens
    cur.output += p.outputTokens
    cur.cost += p.costUsd
    cur.requests += p.requestCount
    map.set(key, cur)
  }
  return [...map.values()].sort((a, b) => b.cost - a.cost || b.input - a.input)
}

function buildDaily(series: SeriesPoint[], from: string, to: string): DailyPoint[] {
  const byDay = new Map<string, DailyPoint>()
  for (const day of eachUtcDay(from, to)) {
    byDay.set(day, { day, input: 0, output: 0, cost: 0 })
  }
  for (const p of series) {
    const day = p.day.slice(0, 10)
    const cur = byDay.get(day) ?? { day, input: 0, output: 0, cost: 0 }
    cur.input += p.inputTokens
    cur.output += p.outputTokens
    cur.cost += p.costUsd
    byDay.set(day, cur)
  }
  return [...byDay.values()].sort((a, b) => a.day.localeCompare(b.day))
}

function summaryFromSeries(
  base: UsageSummary,
  series: SeriesPoint[],
): UsageSummary {
  let costUsd = 0
  let inputTokens = 0
  let outputTokens = 0
  let cacheWriteTokens = 0
  let cacheReadTokens = 0
  let requestCount = 0
  for (const p of series) {
    costUsd += p.costUsd
    inputTokens += p.inputTokens
    outputTokens += p.outputTokens
    cacheWriteTokens += p.cacheWriteTokens
    cacheReadTokens += p.cacheReadTokens
    requestCount += p.requestCount
  }
  return {
    ...base,
    costUsd,
    // Charged er ikke i series — skjul når vi filtrerer til delmængde
    chargedUsd: 0,
    inputTokens,
    outputTokens,
    cacheWriteTokens,
    cacheReadTokens,
    requestCount,
    eventCount: requestCount,
  }
}

function loadEnabledProviders(): Set<string> {
  try {
    const raw = sessionStorage.getItem('aispend.providers')
    if (raw) {
      const parsed = JSON.parse(raw) as string[]
      if (Array.isArray(parsed) && parsed.length > 0) return new Set(parsed.map((p) => p.toLowerCase()))
    }
  } catch {
    /* ignore */
  }
  return new Set(DASHBOARD_PROVIDERS)
}

export function OverviewPage() {
  const [preset, setPreset] = useState<RangePreset>(DEFAULT_RANGE_PRESET)
  const range = useMemo(() => rangeFromPreset(preset), [preset])
  const [enabledProviders, setEnabledProviders] = useState<Set<string>>(loadEnabledProviders)
  const [summary, setSummary] = useState<UsageSummary | null>(null)
  const [modelSeries, setModelSeries] = useState<SeriesPoint[]>([])
  const [providerSeries, setProviderSeries] = useState<SeriesPoint[]>([])
  const [meters, setMeters] = useState<LiveMeter[]>([])
  const [error, setError] = useState<string | null>(null)
  const [loading, setLoading] = useState(true)

  const toggleProvider = useCallback((provider: DashboardProvider) => {
    setEnabledProviders((prev) => {
      const next = new Set(prev)
      if (next.has(provider)) {
        if (next.size <= 1) return prev
        next.delete(provider)
      } else {
        next.add(provider)
      }
      try {
        sessionStorage.setItem('aispend.providers', JSON.stringify([...next]))
      } catch {
        /* ignore */
      }
      return next
    })
  }, [])

  useEffect(() => {
    let cancelled = false
    setLoading(true)
    Promise.all([
      api.summary(range.from, range.to),
      api.series(range.from, range.to, 'model'),
      api.series(range.from, range.to, 'provider'),
      api.meters(),
    ])
      .then(([s, models, providers, m]) => {
        if (cancelled) return
        setSummary(s)
        setModelSeries(models)
        setProviderSeries(providers)
        setMeters(m)
        setError(null)
      })
      .catch((e: Error) => {
        if (!cancelled) setError(e.message)
      })
      .finally(() => {
        if (!cancelled) setLoading(false)
      })
    return () => {
      cancelled = true
    }
  }, [range.from, range.to])

  const availableProviders = useMemo(() => {
    const s = new Set<string>()
    for (const p of providerSeries) s.add(pointProvider(p))
    for (const m of meters) s.add(m.provider.toLowerCase())
    return s
  }, [providerSeries, meters])

  const filteredModels = useMemo(
    () => filterSeries(modelSeries, enabledProviders),
    [modelSeries, enabledProviders],
  )
  const filteredProviders = useMemo(
    () => filterSeries(providerSeries, enabledProviders),
    [providerSeries, enabledProviders],
  )
  const filteredMeters = useMemo(
    () => meters.filter((m) => enabledProviders.has(m.provider.toLowerCase())),
    [meters, enabledProviders],
  )

  const filteredSummary = useMemo(() => {
    if (!summary) return null
    const allOn = DASHBOARD_PROVIDERS.every((p) => enabledProviders.has(p))
    if (allOn) return summary
    const source = filteredModels.length ? filteredModels : filteredProviders
    return summaryFromSeries(summary, source)
  }, [summary, enabledProviders, filteredModels, filteredProviders])

  const models = useMemo(() => aggregateModels(filteredModels), [filteredModels])
  const providers = useMemo(() => aggregateProviders(filteredProviders), [filteredProviders])
  const daily = useMemo(
    () =>
      buildDaily(
        filteredModels.length ? filteredModels : filteredProviders,
        range.from,
        range.to,
      ),
    [filteredModels, filteredProviders, range.from, range.to],
  )

  return (
    <div className="page overview dash">
      <header className="dash-head">
        <div>
          <p className="eyebrow">{rangePresetLabel(preset, range)}</p>
          <h1>AI-Spend</h1>
          <p className="lede">Tokens, modeller og omkostninger — Cursor, Codex og OpenAI.</p>
        </div>
        <div className="dash-filters">
          <RangeChips preset={preset} onChange={setPreset} />
          <ProviderToggles
            enabled={enabledProviders}
            available={availableProviders}
            onToggle={toggleProvider}
          />
        </div>
      </header>

      {loading && !summary && <p className="muted dash-status">Henter forbrug…</p>}
      {error && <p className="error dash-status">{error}</p>}

      {filteredSummary && (
        <>
          <KpiHero summary={filteredSummary} />

          <section className="dash-section">
            <h2>Forbrug pr. dag</h2>
            <DailyUsageChart days={daily} />
          </section>

          <div className="dash-split">
            <section className="dash-section">
              <h2>Providers</h2>
              <ProviderBreakdown rows={providers} />
            </section>
            <section className="dash-section">
              <h2>Live meters</h2>
              <MeterPanel meters={filteredMeters} />
            </section>
          </div>

          <section className="dash-section">
            <h2>Modeller</h2>
            <ModelLeaderboard rows={models} />
          </section>
        </>
      )}
    </div>
  )
}
