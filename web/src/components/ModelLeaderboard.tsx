import { formatUsd } from '../api'
import { TokenCount } from './TokenCount'

export type ModelStat = {
  model: string
  input: number
  output: number
  cost: number
  requests: number
}

const TOP_N = 12

export function ModelLeaderboard({ rows }: { rows: ModelStat[] }) {
  const sorted = [...rows].sort((a, b) => b.cost - a.cost || b.input - a.input)
  const top = sorted.slice(0, TOP_N)
  const rest = sorted.slice(TOP_N)
  const other: ModelStat | null =
    rest.length > 0
      ? {
          model: `Øvrige (${rest.length})`,
          input: rest.reduce((s, r) => s + r.input, 0),
          output: rest.reduce((s, r) => s + r.output, 0),
          cost: rest.reduce((s, r) => s + r.cost, 0),
          requests: rest.reduce((s, r) => s + r.requests, 0),
        }
      : null

  const display = other ? [...top, other] : top
  const maxCost = Math.max(1, ...display.map((r) => r.cost))
  const maxTokens = Math.max(1, ...display.map((r) => r.input + r.output))

  if (display.length === 0) {
    return (
      <p className="muted">Ingen data endnu — tilføj en Cursor/OpenAI/Codex-forbindelse.</p>
    )
  }

  return (
    <ol className="dash-models">
      {display.map((r, i) => {
        const tokens = r.input + r.output
        const shareBy = r.cost > 0 ? r.cost / maxCost : tokens / maxTokens
        return (
          <li key={r.model} className="dash-model-row">
            <span className="dash-model-rank">{i + 1}</span>
            <div className="dash-model-main">
              <div className="dash-model-head">
                <span className="dash-model-name" title={r.model}>
                  {r.model}
                </span>
                <span className="dash-model-cost">{formatUsd(r.cost)}</span>
              </div>
              <div className="dash-share" aria-hidden>
                <i className="dash-share-fill model" style={{ width: `${shareBy * 100}%` }} />
              </div>
              <div className="dash-model-foot">
                <span>
                  in <TokenCount value={r.input} />
                </span>
                <span>
                  out <TokenCount value={r.output} />
                </span>
                <span>
                  <TokenCount value={r.requests} suffix="req" />
                </span>
              </div>
            </div>
          </li>
        )
      })}
    </ol>
  )
}
