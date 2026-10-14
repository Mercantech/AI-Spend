import { RANGE_PRESET_OPTIONS, type RangePreset } from '../api'

export function RangeChips({
  preset,
  onChange,
}: {
  preset: RangePreset
  onChange: (preset: RangePreset) => void
}) {
  return (
    <div className="dash-chips" role="group" aria-label="Periode">
      {RANGE_PRESET_OPTIONS.map((o) => (
        <button
          key={o.id}
          type="button"
          className={`dash-chip${preset === o.id ? ' is-active' : ''}`}
          onClick={() => onChange(o.id)}
          aria-pressed={preset === o.id}
        >
          {o.label}
        </button>
      ))}
    </div>
  )
}
