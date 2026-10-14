import { useId, useMemo, useState } from 'react'
import { formatTokens, formatTokensCompact, formatUsd } from '../api'

export type DailyPoint = {
  day: string
  input: number
  output: number
  cost: number
}

type Hover = { i: number; x: number; y: number } | null

function shortDay(iso: string): string {
  const d = iso.slice(5, 10)
  return d
}

export function DailyUsageChart({ days }: { days: DailyPoint[] }) {
  const gradId = useId().replace(/:/g, '')
  const [hover, setHover] = useState<Hover>(null)

  const { maxTokens, areaPath, linePath, points } = useMemo(() => {
    const maxT = Math.max(1, ...days.map((d) => d.input + d.output))
    const maxC = Math.max(0.01, ...days.map((d) => d.cost))
    const w = 100
    const h = 100
    const n = Math.max(1, days.length)
    const gap = 0.15
    const slot = w / n
    const barW = slot * (1 - gap)

    const pts = days.map((d, i) => {
      const cx = slot * i + slot / 2
      const cy = h - (d.cost / maxC) * (h * 0.92)
      return { ...d, i, cx, cy, barX: slot * i + (slot - barW) / 2, barW }
    })

    const line = pts.map((p, i) => `${i === 0 ? 'M' : 'L'} ${p.cx.toFixed(2)} ${p.cy.toFixed(2)}`).join(' ')
    const area =
      pts.length === 0
        ? ''
        : `${line} L ${pts[pts.length - 1]!.cx.toFixed(2)} ${h} L ${pts[0]!.cx.toFixed(2)} ${h} Z`

    return { maxTokens: maxT, areaPath: area, linePath: line, points: pts }
  }, [days])

  const active = hover ? points[hover.i] : null

  if (days.length === 0) {
    return <p className="muted">Ingen daglige data i perioden.</p>
  }

  return (
    <div className="dash-chart">
      <svg
        className="dash-chart-svg"
        viewBox="0 0 100 100"
        preserveAspectRatio="none"
        role="img"
        aria-label="Tokens og omkostning pr. dag"
        onMouseLeave={() => setHover(null)}
      >
        <defs>
          <linearGradient id={`costGrad-${gradId}`} x1="0" y1="0" x2="0" y2="1">
            <stop offset="0%" stopColor="var(--accent)" stopOpacity="0.35" />
            <stop offset="100%" stopColor="var(--accent)" stopOpacity="0" />
          </linearGradient>
        </defs>

        {points.map((p) => {
          const total = p.input + p.output
          const barH = (total / maxTokens) * 88
          const inH = total ? (p.input / total) * barH : 0
          const outH = barH - inH
          const base = 100
          return (
            <g
              key={p.day}
              className="dash-chart-bar"
              onMouseEnter={(e) => {
                const rect = (e.currentTarget.ownerSVGElement as SVGSVGElement).getBoundingClientRect()
                setHover({
                  i: p.i,
                  x: e.clientX - rect.left,
                  y: e.clientY - rect.top,
                })
              }}
              onMouseMove={(e) => {
                const rect = (e.currentTarget.ownerSVGElement as SVGSVGElement).getBoundingClientRect()
                setHover({
                  i: p.i,
                  x: e.clientX - rect.left,
                  y: e.clientY - rect.top,
                })
              }}
            >
              <rect
                x={p.barX}
                y={base - outH - inH}
                width={p.barW}
                height={Math.max(0.4, inH)}
                className="dash-bar-in"
              />
              <rect
                x={p.barX}
                y={base - outH}
                width={p.barW}
                height={Math.max(0.4, outH)}
                className="dash-bar-out"
              />
              <rect x={p.barX} y={0} width={p.barW} height={100} fill="transparent" />
            </g>
          )
        })}

        {areaPath && (
          <path d={areaPath} fill={`url(#costGrad-${gradId})`} className="dash-cost-area" />
        )}
        {linePath && (
          <path d={linePath} fill="none" className="dash-cost-line" vectorEffect="non-scaling-stroke" />
        )}

        {active && (
          <circle
            cx={active.cx}
            cy={active.cy}
            r={1.2}
            className="dash-cost-dot"
            vectorEffect="non-scaling-stroke"
          />
        )}
      </svg>

      <div className="dash-chart-axis">
        <span>{shortDay(days[0]!.day)}</span>
        {days.length > 2 && <span>{shortDay(days[Math.floor(days.length / 2)]!.day)}</span>}
        <span>{shortDay(days[days.length - 1]!.day)}</span>
      </div>

      <p className="muted dash-chart-legend">
        <span className="dot in" /> input <span className="dot out" /> output
        <span className="dash-legend-cost" /> cost (linje)
      </p>

      {active && hover && (
        <div
          className="dash-tooltip"
          style={{ left: Math.min(hover.x + 12, 280), top: Math.max(8, hover.y - 8) }}
        >
          <strong>{active.day}</strong>
          <div>Cost {formatUsd(active.cost)}</div>
          <div title={formatTokens(active.input)}>In {formatTokensCompact(active.input)}</div>
          <div title={formatTokens(active.output)}>Out {formatTokensCompact(active.output)}</div>
        </div>
      )}
    </div>
  )
}
