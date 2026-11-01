/**
 * A moment in words relative to now, in compact units: "in 3d 2h 1m", "5m ago", "45s ago". Days, hours and minutes
 * that are not zero are shown, largest first; seconds only when less than a minute is left. Nothing is rounded up, so
 * "in 59s" never reads as a minute. Returns an empty string for a missing or invalid value.
 */
export function relativeTime(iso: string | null | undefined, now: number = Date.now()): string {
  if (!iso) return ''
  const time = new Date(iso.endsWith('Z') ? iso : iso.replace(' ', 'T') + 'Z').getTime()
  if (Number.isNaN(time)) return ''

  const difference = time - now
  const totalSeconds = Math.floor(Math.abs(difference) / 1000)
  if (totalSeconds < 1) return 'now'

  const parts: string[] = []
  if (totalSeconds < 60) {
    parts.push(`${totalSeconds}s`)
  } else {
    const days = Math.floor(totalSeconds / 86_400)
    const hours = Math.floor((totalSeconds % 86_400) / 3_600)
    const minutes = Math.floor((totalSeconds % 3_600) / 60)
    if (days > 0) parts.push(`${days}d`)
    if (hours > 0) parts.push(`${hours}h`)
    if (minutes > 0) parts.push(`${minutes}m`)
  }

  const text = parts.join(' ')
  return difference > 0 ? `in ${text}` : `${text} ago`
}
