/**
 * A time the operator types in the dashboard is a wall-clock time in the scheduler's time zone. It is sent without a
 * zone ("2026-12-01T23:00:00") together with timeZoneId, and the server reads it in that zone. Converting it to UTC in
 * the browser would read it in the browser's zone instead, which moves the job when the two zones differ.
 */

/**
 * The wall-clock text for a picked calendar day and a typed time "HH:mm" or "HH:mm:ss". The day is read in the
 * browser's own calendar (a date picker gives local midnight); the time is used as typed. Returns undefined when
 * either is missing.
 */
export function toWallClockText(date: Date | null | undefined, time: string | null | undefined): string | undefined {
  // A Date only: a string such as "2026-12-01" would be read as UTC midnight, the day before west of UTC.
  if (!date || !time) return undefined
  const day = date
  if (Number.isNaN(day.getTime())) return undefined
  const [hours = 0, minutes = 0, seconds = 0] = time.split(':').map(Number)
  const pad = (n: number) => String(n).padStart(2, '0')
  return `${day.getFullYear()}-${pad(day.getMonth() + 1)}-${pad(day.getDate())}T${pad(hours)}:${pad(minutes)}:${pad(seconds)}`
}
