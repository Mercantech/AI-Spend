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
      setStatus('Forbindelse gemt og valideret.')
    } catch (err) {
      setError(parseErr(err))
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
      } else if (c.provider === 'codex') {
        const r = await api.backfillCodex({ connectionId: c.id })
        setStatus(
          `Codex sync: ${r.upserted} dag-buckets, ${r.meters} meters` +
            (r.lifetimeTokens != null ? `, lifetime ${r.lifetimeTokens.toLocaleString('da-DK')} tokens.` : '.'),
        )
      }
      await reload()
    } catch (err) {
      setError(parseErr(err))
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
      setError(parseErr(err))
    } finally {
      setBusy(false)
    }
  }

  const secretLabel =
    provider === 'cursor'
      ? 'Session-cookie eller Team Admin-nøgle'
      : provider === 'codex'
        ? 'Codex auth.json eller access_token'
        : 'API-nøgle'

  const secretPlaceholder =
    provider === 'cursor'
      ? 'WorkosCursorSessionToken=… eller crsr_…'
      : provider === 'codex'
        ? 'Paste ~/.codex/auth.json eller access_token'
        : 'OpenAI Admin key'

  return (
    <div className="page">
      <header className="page-head">
        <h1>Forbindelser</h1>
        <p className="lede">Nøgler/cookies gemmes krypteret på serveren.</p>
      </header>

      {provider === 'cursor' && (
        <div className="hint-box">
          <p>
            <strong>Personligt Cursor-forbrug:</strong> åbn{' '}
            <a href="https://cursor.com/dashboard/usage" target="_blank" rel="noreferrer">
              cursor.com/dashboard/usage
            </a>
            , F12 → Application → Cookies → kopiér <code>WorkosCursorSessionToken</code>.
          </p>
          <p>
            <strong>Team (Enterprise):</strong> Team Admin API-nøgle (`crsr_…`) — ikke User API-nøgle.
          </p>
        </div>
      )}

      {provider === 'codex' && (
        <div className="hint-box">
          <p>
            <strong>Anbefalet:</strong> kør <code>codex login</code>, åbn{' '}
            <code>%USERPROFILE%\.codex\auth.json</code> (Windows) eller{' '}
            <code>~/.codex/auth.json</code>, og paste <em>hele filen</em> her. Vi bruger{' '}
            <code>tokens.access_token</code> (undgår Cloudflare-blokering af browser-cookies).
          </p>
          <p>
            Alternativt: kun <code>tokens.access_token</code>-værdien (JWT med tre dele — ikke den
            lange JWE-cookie fra chatgpt.com).
          </p>
          <p>
            Session-cookie fra chatgpt.com virker ofte <em>ikke</em> fra serveren (Cloudflare 403).
          </p>
        </div>
      )}

      <form className="conn-form" onSubmit={onSubmit}>
        <label>
          Provider
          <select value={provider} onChange={(e) => setProvider(e.target.value)}>
            <option value="cursor">Cursor</option>
            <option value="codex">Codex (ChatGPT)</option>
            <option value="openai">OpenAI Admin</option>
          </select>
        </label>
        <label>
          Label
          <input value={label} onChange={(e) => setLabel(e.target.value)} placeholder="Valgfri" />
        </label>
        <label className="grow">
          {secretLabel}
          <input
            type="password"
            value={apiKey}
            onChange={(e) => setApiKey(e.target.value)}
            required
            placeholder={secretPlaceholder}
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

function parseErr(err: unknown): string {
  if (!(err instanceof Error)) return 'Fejl'
  try {
    const j = JSON.parse(err.message) as { error?: string }
    if (j.error) return j.error
  } catch {
    /* plain text */
  }
  return err.message
}
