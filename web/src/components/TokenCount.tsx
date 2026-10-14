import { formatTokens, formatTokensCompact } from '../api'

/** Kompakt token-tal; hover (title) viser det præcise tal. */
export function TokenCount({
  value,
  exact,
  className,
  suffix,
}: {
  value: number
  /** Fast hover-værdi (fx endeligt KPI under count-up). */
  exact?: number
  className?: string
  suffix?: string
}) {
  const tip = formatTokens(exact ?? value)
  const compact = formatTokensCompact(value)
  return (
    <span className={className ? `token-count ${className}` : 'token-count'} title={tip}>
      {compact}
      {suffix ? ` ${suffix}` : ''}
    </span>
  )
}
