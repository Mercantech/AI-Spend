import { useEffect, useState } from 'react'
import { Link, Navigate, Route, Routes, useNavigate } from 'react-router-dom'
import { handleCallback, isLoggedIn, logoutLocal, startLogin } from './auth'
import { ConnectionsPage } from './pages/ConnectionsPage'
import { EventsPage } from './pages/EventsPage'
import { OverviewPage } from './pages/OverviewPage'

function RequireAuth({ children }: { children: React.ReactNode }) {
  if (!isLoggedIn()) return <Navigate to="/login" replace />
  return <>{children}</>
}

function LoginPage() {
  return (
    <div className="auth-shell">
      <div className="auth-panel">
        <p className="brand">AI-Spend</p>
        <h1>Hold øje med AI-forbrug</h1>
        <p className="lede">Tokens, modeller og omkostninger fra Cursor og OpenAI.</p>
        <button type="button" className="btn primary" onClick={() => void startLogin()}>
          Log ind
        </button>
      </div>
    </div>
  )
}

function CallbackPage() {
  const navigate = useNavigate()
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    void handleCallback()
      .then(() => navigate('/', { replace: true }))
      .catch((e: Error) => setError(e.message))
  }, [navigate])

  if (error) {
    return (
      <div className="auth-shell">
        <div className="auth-panel">
          <p className="brand">AI-Spend</p>
          <h1>Login fejlede</h1>
          <p>{error}</p>
          <Link to="/login">Prøv igen</Link>
        </div>
      </div>
    )
  }

  return (
    <div className="auth-shell">
      <div className="auth-panel">
        <p className="brand">AI-Spend</p>
        <p>Logger ind…</p>
      </div>
    </div>
  )
}

function Shell({ children }: { children: React.ReactNode }) {
  return (
    <div className="app-shell">
      <header className="topbar">
        <Link to="/" className="topbar-brand">
          <span className="brand">AI-Spend</span>
        </Link>
        <nav>
          <Link to="/">Oversigt</Link>
          <Link to="/events">Events</Link>
          <Link to="/connections">Forbindelser</Link>
        </nav>
        <button type="button" className="btn ghost" onClick={() => logoutLocal()}>
          Log ud
        </button>
      </header>
      <main>{children}</main>
    </div>
  )
}

export default function App() {
  return (
    <Routes>
      <Route path="/login" element={<LoginPage />} />
      <Route path="/callback" element={<CallbackPage />} />
      <Route
        path="/"
        element={
          <RequireAuth>
            <Shell>
              <OverviewPage />
            </Shell>
          </RequireAuth>
        }
      />
      <Route
        path="/events"
        element={
          <RequireAuth>
            <Shell>
              <EventsPage />
            </Shell>
          </RequireAuth>
        }
      />
      <Route
        path="/connections"
        element={
          <RequireAuth>
            <Shell>
              <ConnectionsPage />
            </Shell>
          </RequireAuth>
        }
      />
      <Route path="*" element={<Navigate to="/" replace />} />
    </Routes>
  )
}
