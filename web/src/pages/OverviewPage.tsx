import { useEffect, useMemo, useState } from 'react'
import {
  api,
  defaultRange,
  formatTokens,
  formatUsd,
  type LiveMeter,
  type SeriesPoint,
  type UsageSummary,
} from '../api'

export function OverviewPage() {
  const range = useMemo(() => defaultRange(), [])
  const [summary, setSummary] = useState<UsageSummary | null>(null)
  const [series, setSeries] = useState<SeriesPoint[]>([])
  const [meters, setMeters] = useState<LiveMeter[]>([])
  const [error, setError] = useState<string | null>(null)
  const [loading, setLoading] = useState(true)

  useEffect(() => {
    let cancelled = false
    setLoading(true)
    Promise.all([
      api.summary(range.from, range.to),
      api.series(range.from, range.to, 'model'),
      api.meters(),
    ])
      .then(([s, ser, m]) => {
        if (cancelled) return
        setSummary(s)
        setSeries(ser)
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

  const byModel = useMemo(() => {
    const map = new Map<string, { input: number; output: number; cost: number; requests: number }>()
    for (const p of series) {
      const cur = map.get(p.key) ?? { input: 0, output: 0, cost: 0, requests: 0 }
      cur.input += p.inputTokens
      cur.output += p.outputTokens
      cur.cost += p.costUsd
      cur.requests += p.requestCount
      map.set(p.key, cur)
    }
    return [...map.entries()].sort((a, b) => b[1].cost - a[1].cost || b[1].input - a[1].input)
  }, [series])

  const maxDayTokens = useMemo(() => {
    const byDay = new Map<string, number>()
    for (const p of series) {
      byDay.set(p.day, (byDay.get(p.day) ?? 0) + p.inputTokens + p.outputTokens)
    }
    return Math.max(1, ...byDay.values())
  }, [series])

  const dayBars = useMemo(() => {
    const byDay = new Map<string, { input: number; output: number }>()
    for (const p of series) {
      const cur = byDay.get(p.day) ?? { input: 0, output: 0 }
      cur.input += p.inputTokens
      cur.output += p.outputTokens
      byDay.set(p.day, cur)
    }
    return [...byDay.entries()].sort((a, b) => a[0].localeCompare(b[0]))
  }, [series])

  if (loading) return <p className="muted">Henter forbrug…</p>
  if (error) return <p className="error">{error}</p>

  return (
    <div className="page overview">
      <section className="hero-spend">
        <p className="eyebrow">Seneste 30 dage</p>
        <h1>AI-Spend</h1>
        <p className="lede">Tokens, modeller og omkostninger — samlet for Cursor og OpenAI.</p>
        <div className="stat-row">
          <div>
            <span className="stat-label">Omkostning</span>
            <strong>{formatUsd(summary?.costUsd ?? 0)}</strong>
          </div>
          <div>
            <span className="stat-label">Input tokens</span>
            <strong>{formatTokens(summary?.inputTokens ?? 0)}</strong>
          </div>
          <div>
            <span className="stat-label">Output tokens</span>
            <strong>{formatTokens(summary?.outputTokens ?? 0)}</strong>
          </div>
          <div>
            <span className="stat-label">Events</span>
            <strong>{formatTokens(summary?.eventCount ?? 0)}</strong>
          </div>
        </div>
      </section>

      {meters.length > 0 && (
        <section>
          <h2>Live meters</h2>
          <div className="meter-grid">
            {meters.map((m) => (
              <div key={`${m.provider}-${m.label}`} className="meter">
                <div className="meter-head">
                  <span>{m.provider}</span>
                  <span>{m.label}</span>
                </div>
                <div className="meter-bar">
                  <i style={{ width: `${Math.min(100, m.usedPercent)}%` }} />
                </div>
                <div className="meter-foot">
                  <span>{m.usedPercent.toFixed(1)}%</span>
                  {m.displayValue && <span>{m.displayValue}</span>}
                </div>
              </div>
            ))}
          </div>
        </section>
      )}

      <section>
        <h2>Tokens pr. dag</h2>
        <div className="spark">
          {dayBars.map(([day, v]) => {
            const total = v.input + v.output
            const h = (total / maxDayTokens) * 100
            const inPct = total ? (v.input / total) * 100 : 50
            return (
              <div key={day} className="spark-col" title={`${day}: ${formatTokens(total)}`}>
                <div className="spark-bar" style={{ height: `${Math.max(4, h)}%` }}>
                  <i className="in" style={{ height: `${inPct}%` }} />
                  <i className="out" style={{ height: `${100 - inPct}%` }} />
                </div>
              </div>
            )
          })}
        </div>
        <p className="muted spark-legend">
          <span className="dot in" /> input <span className="dot out" /> output
        </p>
      </section>

      <section>
        <h2>Modeller</h2>
        <table className="data">
          <thead>
            <tr>
              <th>Model</th>
              <th>Input</th>
              <th>Output</th>
              <th>Requests</th>
              <th>Cost</th>
            </tr>
          </thead>
          <tbody>
            {byModel.map(([model, v]) => (
              <tr key={model}>
                <td>{model}</td>
                <td>{formatTokens(v.input)}</td>
                <td>{formatTokens(v.output)}</td>
                <td>{formatTokens(v.requests)}</td>
                <td>{formatUsd(v.cost)}</td>
              </tr>
            ))}
            {byModel.length === 0 && (
              <tr>
                <td colSpan={5} className="muted">
                  Ingen data endnu — tilføj en Cursor/OpenAI-forbindelse.
                </td>
              </tr>
            )}
          </tbody>
        </table>
      </section>
    </div>
  )
}
