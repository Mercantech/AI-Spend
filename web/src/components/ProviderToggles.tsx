import { DASHBOARD_PROVIDERS, providerLabel, type DashboardProvider } from '../api'

export function ProviderToggles({
  enabled,
  available,
  onToggle,
}: {
  enabled: Set<string>
  /** Providers der har data (eller forbindelser) — resten vises muted. */
  available?: Set<string>
  onToggle: (provider: DashboardProvider) => void
}) {
  return (
    <div className="dash-chips dash-provider-toggles" role="group" aria-label="Providers">
      {DASHBOARD_PROVIDERS.map((p) => {
        const on = enabled.has(p)
        const hasData = !available || available.has(p)
        return (
          <button
            key={p}
            type="button"
            className={`dash-chip provider-chip provider-${p}${on ? ' is-active' : ''}${hasData ? '' : ' is-empty'}`}
            onClick={() => onToggle(p)}
            aria-pressed={on}
            title={on ? `Skjul ${providerLabel(p)}` : `Vis ${providerLabel(p)}`}
          >
            {providerLabel(p)}
          </button>
        )
      })}
    </div>
  )
}
