import { formatTokens, formatUsd } from '../api'

export type ProviderStat = {
  provider: string
  input: number
  output: number
  cost: number
  requests: number
}

function label(provider: string): string {
  const p = provider.toLowerCase()
  if (p === 'cursor') return 'Cursor'
  if (p === 'codex') return 'Codex'
  if (p === 'openai') return 'OpenAI'
  return provider
}

export function ProviderBreakdown({ rows }: { rows: ProviderStat[] }) {
  const maxCost = Math.max(1, ...rows.map((r) => r.cost))
  const totalTokens = rows.reduce((s, r) => s + r.input + r.output, 0) || 1

  if (rows.length === 0) {
    return <p className="muted">Ingen provider-data i perioden.</p>
  }

  return (
    <ul className="dash-provider-list">
      {rows.map((r) => {
        const tokens = r.input + r.output
        const tokenShare = (tokens / totalTokens) * 100
        const costShare = (r.cost / maxCost) * 100
        return (
          <li key={r.provider} className="dash-provider-row">
            <div className="dash-provider-head">
              <span className="dash-provider-name">{label(r.provider)}</span>
              <span className="dash-provider-meta">
                {formatUsd(r.cost)} · {formatTokens(tokens)} tok
              </span>
            </div>
            <div className="dash-share" aria-hidden>
              <i className={`dash-share-fill provider-${r.provider.toLowerCase()}`} style={{ width: `${costShare}%` }} />
            </div>
            <div className="dash-provider-foot">
              <span>{tokenShare.toFixed(0)}% af tokens</span>
              <span>{formatTokens(r.requests)} req</span>
            </div>
          </li>
        )
      })}
    </ul>
  )
}
