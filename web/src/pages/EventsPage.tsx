import { useEffect, useMemo, useState } from 'react'
import { api, defaultRange, formatTokens, type UsageEvent } from '../api'

export function EventsPage() {
  const range = useMemo(() => defaultRange(), [])
  const [items, setItems] = useState<UsageEvent[]>([])
  const [total, setTotal] = useState(0)
  const [page, setPage] = useState(1)
  const [q, setQ] = useState('')
  const [provider, setProvider] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [loading, setLoading] = useState(true)

  useEffect(() => {
    let cancelled = false
    setLoading(true)
    api
      .events({
        from: range.from,
        to: range.to,
        page,
        pageSize: 50,
        q: q || undefined,
        provider: provider || undefined,
      })
      .then((res) => {
        if (cancelled) return
        setItems(res.items)
        setTotal(res.total)
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
  }, [range.from, range.to, page, q, provider])

  const pages = Math.max(1, Math.ceil(total / 50))

  return (
    <div className="page">
      <header className="page-head">
        <h1>Events</h1>
        <p className="lede">Rå usage-events med input/output/cache-tokens.</p>
      </header>

      <div className="filters">
        <input
          placeholder="Søg model, email, conversation…"
          value={q}
          onChange={(e) => {
            setPage(1)
            setQ(e.target.value)
          }}
        />
        <select
          value={provider}
          onChange={(e) => {
            setPage(1)
            setProvider(e.target.value)
          }}
        >
          <option value="">Alle providere</option>
          <option value="cursor">Cursor</option>
          <option value="openai">OpenAI</option>
        </select>
      </div>

      {loading && <p className="muted">Henter…</p>}
      {error && <p className="error">{error}</p>}

      <table className="data dense">
        <thead>
          <tr>
            <th>Tid</th>
            <th>Provider</th>
            <th>Model</th>
            <th>Input</th>
            <th>Output</th>
            <th>Cache R/W</th>
            <th>Cost ¢</th>
            <th>User</th>
          </tr>
        </thead>
        <tbody>
          {items.map((e) => (
            <tr key={e.id}>
              <td>{new Date(e.occurredAt).toLocaleString('da-DK')}</td>
              <td>{e.provider}</td>
              <td>{e.model ?? '—'}</td>
              <td>{formatTokens(e.inputTokens)}</td>
              <td>{formatTokens(e.outputTokens)}</td>
              <td>
                {formatTokens(e.cacheReadTokens)}/{formatTokens(e.cacheWriteTokens)}
              </td>
              <td>{(e.chargedCents || e.costCents).toFixed(2)}</td>
              <td>{e.userEmail ?? '—'}</td>
            </tr>
          ))}
          {!loading && items.length === 0 && (
            <tr>
              <td colSpan={8} className="muted">
                Ingen events i perioden.
              </td>
            </tr>
          )}
        </tbody>
      </table>

      <div className="pager">
        <button type="button" disabled={page <= 1} onClick={() => setPage((p) => p - 1)}>
          Forrige
        </button>
        <span>
          Side {page} / {pages} ({formatTokens(total)} events)
        </span>
        <button type="button" disabled={page >= pages} onClick={() => setPage((p) => p + 1)}>
          Næste
        </button>
      </div>
    </div>
  )
}
