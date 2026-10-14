import { useMemo } from 'react'
import type { LiveMeter } from '../api'

function formatReset(iso: string | null): string | null {
  if (!iso) return null
  const t = new Date(iso).getTime()
  if (Number.isNaN(t)) return null
  const diff = t - Date.now()
  if (diff <= 0) return 'reset nu'
  const h = Math.floor(diff / 3_600_000)
  const m = Math.floor((diff % 3_600_000) / 60_000)
  if (h >= 48) return `reset om ${Math.round(h / 24)}d`
  if (h >= 1) return `reset om ${h}t ${m}m`
  return `reset om ${m}m`
}

export function MeterPanel({ meters }: { meters: LiveMeter[] }) {
  const groups = useMemo(() => {
    const map = new Map<string, LiveMeter[]>()
    for (const m of meters) {
      const list = map.get(m.provider) ?? []
      list.push(m)
      map.set(m.provider, list)
    }
    return [...map.entries()].sort((a, b) => a[0].localeCompare(b[0]))
  }, [meters])

  if (meters.length === 0) {
    return <p className="muted">Ingen live meters endnu.</p>
  }

  return (
    <div className="dash-meters">
      {groups.map(([provider, items]) => (
        <div key={provider} className="dash-meter-group">
          <h3 className="dash-meter-provider">{provider}</h3>
          <ul>
            {items.map((m) => {
              const pct = Math.min(100, Math.max(0, m.usedPercent || 0))
              const reset = formatReset(m.resetsAt)
              return (
                <li key={`${m.provider}-${m.label}`} className="dash-meter">
                  <div className="dash-meter-head">
                    <span>{m.label}</span>
                    <span className="dash-meter-pct">{pct.toFixed(1)}%</span>
                  </div>
                  <div className="dash-meter-track">
                    <i style={{ width: `${pct}%` }} />
                  </div>
                  <div className="dash-meter-foot">
                    {m.displayValue && <span>{m.displayValue}</span>}
                    {reset && <span className="muted">{reset}</span>}
                  </div>
                </li>
              )
            })}
          </ul>
        </div>
      ))}
    </div>
  )
}
