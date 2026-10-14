const OPTIONS: { days: number; label: string }[] = [
  { days: 7, label: '7d' },
  { days: 30, label: '30d' },
  { days: 90, label: '90d' },
  { days: 730, label: '2år' },
]

export function RangeChips({
  days,
  onChange,
}: {
  days: number
  onChange: (days: number) => void
}) {
  return (
    <div className="dash-chips" role="group" aria-label="Periode">
      {OPTIONS.map((o) => (
        <button
          key={o.days}
          type="button"
          className={`dash-chip${days === o.days ? ' is-active' : ''}`}
          onClick={() => onChange(o.days)}
          aria-pressed={days === o.days}
        >
          {o.label}
        </button>
      ))}
    </div>
  )
}
