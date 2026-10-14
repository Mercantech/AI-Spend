import { useEffect, useMemo, useState } from 'react'
import { DailyUsageChart, type DailyPoint } from '../components/DailyUsageChart'
import { KpiHero } from '../components/KpiHero'
import { MeterPanel } from '../components/MeterPanel'
import { ModelLeaderboard, type ModelStat } from '../components/ModelLeaderboard'
import { ProviderBreakdown, type ProviderStat } from '../components/ProviderBreakdown'
import { RangeChips } from '../components/RangeChips'
import {
  api,
  DEFAULT_DASHBOARD_DAYS,
  rangeFromDays,
  type LiveMeter,
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
    const key = p.key || 'unknown'
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

export function OverviewPage() {
  const [days, setDays] = useState(DEFAULT_DASHBOARD_DAYS)
  const range = useMemo(() => rangeFromDays(days), [days])
  const [summary, setSummary] = useState<UsageSummary | null>(null)
  const [modelSeries, setModelSeries] = useState<SeriesPoint[]>([])
  const [providerSeries, setProviderSeries] = useState<SeriesPoint[]>([])
  const [meters, setMeters] = useState<LiveMeter[]>([])
  const [error, setError] = useState<string | null>(null)
  const [loading, setLoading] = useState(true)

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

  const models = useMemo(() => aggregateModels(modelSeries), [modelSeries])
  const providers = useMemo(() => aggregateProviders(providerSeries), [providerSeries])
  const daily = useMemo(
    () => buildDaily(modelSeries.length ? modelSeries : providerSeries, range.from, range.to),
    [modelSeries, providerSeries, range.from, range.to],
  )

  const periodLabel =
    days === 7 ? 'Seneste 7 dage' : days === 30 ? 'Seneste 30 dage' : days === 90 ? 'Seneste 90 dage' : 'Seneste 2 år'

  return (
    <div className="page overview dash">
      <header className="dash-head">
        <div>
          <p className="eyebrow">{periodLabel}</p>
          <h1>AI-Spend</h1>
          <p className="lede">Tokens, modeller og omkostninger — Cursor, Codex og OpenAI.</p>
        </div>
        <RangeChips days={days} onChange={setDays} />
      </header>

      {loading && !summary && <p className="muted dash-status">Henter forbrug…</p>}
      {error && <p className="error dash-status">{error}</p>}

      {summary && (
        <>
          <KpiHero summary={summary} />

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
              <MeterPanel meters={meters} />
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
