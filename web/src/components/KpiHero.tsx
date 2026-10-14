import { useEffect, useState } from 'react'
import { formatTokens, formatUsd, type UsageSummary } from '../api'

function usePrefersReducedMotion(): boolean {
  const [reduced, setReduced] = useState(false)
  useEffect(() => {
    const mq = window.matchMedia('(prefers-reduced-motion: reduce)')
    setReduced(mq.matches)
    const onChange = () => setReduced(mq.matches)
    mq.addEventListener('change', onChange)
    return () => mq.removeEventListener('change', onChange)
  }, [])
  return reduced
}

function useCountUp(target: number, durationMs = 700): number {
  const reduced = usePrefersReducedMotion()
  const [value, setValue] = useState(reduced ? target : 0)

  useEffect(() => {
    if (reduced) {
      setValue(target)
      return
    }
    let frame = 0
    const start = performance.now()
    const from = 0
    const tick = (now: number) => {
      const t = Math.min(1, (now - start) / durationMs)
      const eased = 1 - (1 - t) ** 3
      setValue(from + (target - from) * eased)
      if (t < 1) frame = requestAnimationFrame(tick)
    }
    frame = requestAnimationFrame(tick)
    return () => cancelAnimationFrame(frame)
  }, [target, durationMs, reduced])

  return value
}

export function KpiHero({ summary }: { summary: UsageSummary }) {
  const cost = useCountUp(summary.costUsd)
  const input = useCountUp(summary.inputTokens)
  const output = useCountUp(summary.outputTokens)
  const events = useCountUp(summary.eventCount)

  const cacheDenom = summary.inputTokens + summary.cacheReadTokens
  const cacheHit = cacheDenom > 0 ? (summary.cacheReadTokens / cacheDenom) * 100 : 0

  return (
    <section className="dash-kpi">
      <div className="dash-kpi-primary">
        <span className="dash-kpi-label">Omkostning</span>
        <strong className="dash-kpi-cost">{formatUsd(cost)}</strong>
        {summary.chargedUsd > 0 && summary.chargedUsd !== summary.costUsd && (
          <span className="dash-kpi-sub">Charged {formatUsd(summary.chargedUsd)}</span>
        )}
      </div>
      <div className="dash-kpi-grid">
        <div>
          <span className="dash-kpi-label">Input tokens</span>
          <strong>{formatTokens(Math.round(input))}</strong>
        </div>
        <div>
          <span className="dash-kpi-label">Output tokens</span>
          <strong>{formatTokens(Math.round(output))}</strong>
        </div>
        <div>
          <span className="dash-kpi-label">Events</span>
          <strong>{formatTokens(Math.round(events))}</strong>
        </div>
        <div>
          <span className="dash-kpi-label">Cache hit</span>
          <strong>{cacheHit.toFixed(1)}%</strong>
          <span className="dash-kpi-sub">
            {formatTokens(summary.cacheReadTokens)} read
            {summary.cacheWriteTokens > 0 ? ` · ${formatTokens(summary.cacheWriteTokens)} write` : ''}
          </span>
        </div>
      </div>
    </section>
  )
}
