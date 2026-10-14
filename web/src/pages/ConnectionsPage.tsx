import { useEffect, useState, type FormEvent } from 'react'
import { api, type Connection } from '../api'

export function ConnectionsPage() {
  const [items, setItems] = useState<Connection[]>([])
  const [provider, setProvider] = useState('cursor')
  const [label, setLabel] = useState('')
  const [apiKey, setApiKey] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [status, setStatus] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  async function reload() {
    const list = await api.connections()
    setItems(list)
  }

  useEffect(() => {
    void reload().catch((e: Error) => setError(e.message))
  }, [])

  async function onSubmit(e: FormEvent) {
    e.preventDefault()
    setBusy(true)
    setError(null)
    setStatus(null)
    try {
      await api.createConnection({
        provider,
        apiKey,
        label: label || undefined,
      })
      setApiKey('')
      setLabel('')
      await reload()
      setStatus('Forbindelse gemt.')
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Fejl')
    } finally {
      setBusy(false)
    }
  }

  async function backfill(c: Connection) {
    setBusy(true)
    setError(null)
    setStatus(null)
    try {
      if (c.provider === 'cursor') {
        const r = await api.backfillCursor({ connectionId: c.id })
        setStatus(`Cursor backfill: ${r.upserted} events (${r.rawStored} raw).`)
      } else if (c.provider === 'openai') {
        const r = await api.backfillOpenAi({ connectionId: c.id })
        setStatus(`OpenAI backfill: ${r.upserted} buckets, ${r.costRows} cost-dage.`)
      }
      await reload()
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Fejl')
    } finally {
      setBusy(false)
    }
  }

  async function remove(c: Connection) {
    if (!confirm(`Slet ${c.label}?`)) return
    setBusy(true)
    try {
      await api.deleteConnection(c.id)
      await reload()
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Fejl')
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="page">
      <header className="page-head">
        <h1>Forbindelser</h1>
        <p className="lede">
          Cursor Admin API-nøgle eller OpenAI Admin-nøgle. Nøgler gemmes krypteret på serveren.
        </p>
      </header>

      <form className="conn-form" onSubmit={onSubmit}>
        <label>
          Provider
          <select value={provider} onChange={(e) => setProvider(e.target.value)}>
            <option value="cursor">Cursor</option>
            <option value="openai">OpenAI</option>
          </select>
        </label>
        <label>
          Label
          <input value={label} onChange={(e) => setLabel(e.target.value)} placeholder="Valgfri" />
        </label>
        <label className="grow">
          API-nøgle
          <input
            type="password"
            value={apiKey}
            onChange={(e) => setApiKey(e.target.value)}
            required
            placeholder={provider === 'cursor' ? 'Cursor Admin API key' : 'OpenAI Admin key'}
            autoComplete="off"
          />
        </label>
        <button type="submit" className="btn primary" disabled={busy}>
          Tilføj
        </button>
      </form>

      {error && <p className="error">{error}</p>}
      {status && <p className="ok">{status}</p>}

      <table className="data">
        <thead>
          <tr>
            <th>Provider</th>
            <th>Label</th>
            <th>Seneste sync</th>
            <th>Status</th>
            <th />
          </tr>
        </thead>
        <tbody>
          {items.map((c) => (
            <tr key={c.id}>
              <td>{c.provider}</td>
              <td>{c.label}</td>
              <td>{c.lastSyncAt ? new Date(c.lastSyncAt).toLocaleString('da-DK') : '—'}</td>
              <td className={c.lastSyncError ? 'error' : 'muted'}>
                {c.lastSyncError ?? (c.isActive ? 'Aktiv' : 'Inaktiv')}
              </td>
              <td className="actions">
                <button type="button" disabled={busy} onClick={() => void backfill(c)}>
                  Backfill
                </button>
                <button type="button" className="danger" disabled={busy} onClick={() => void remove(c)}>
                  Slet
                </button>
              </td>
            </tr>
          ))}
          {items.length === 0 && (
            <tr>
              <td colSpan={5} className="muted">
                Ingen forbindelser endnu.
              </td>
            </tr>
          )}
        </tbody>
      </table>
    </div>
  )
}
