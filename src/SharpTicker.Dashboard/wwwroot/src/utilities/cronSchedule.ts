import cronstrue from 'cronstrue'
import { isValidCronExpression } from '@/utilities/cronExpression'

/**
 * The schedule of a cron ticker in words, for the Cron Tickers table: "Every 5 minutes", "At 01:00 AM (scheduler time,
 * UTC)".
 *
 * The scheduler evaluates an expression in its own time zone, not in the one the dashboard shows times in. That zone
 * matters only when the expression is tied to a time of day or to a date; "every 5 minutes" is the same sentence in
 * every zone, and naming a zone there only reads as stale when another display zone is chosen. So the zone is named
 * only for the expressions that depend on it, and the sentence says it is the scheduler's time.
 *
 * Returns null when the expression is empty, not valid, or cronstrue cannot put it in words (for example with a full
 * day name); the table then shows the expression alone.
 */
export function describeSchedule(
  expression: string | null | undefined,
  schedulerTimeZone: string | null | undefined,
): string | null {
  if (!expression || !isValidCronExpression(expression)) return null

  let sentence: string
  try {
    sentence = cronstrue.toString(expression)
  } catch {
    return null
  }

  return schedulerTimeZone && dependsOnTimeZone(expression)
    ? `${sentence} (scheduler time, ${schedulerTimeZone})`
    : sentence
}

/** True when the hour, the day of the month, the month or the day of the week is fixed, so the zone decides when it fires. */
export function dependsOnTimeZone(expression: string): boolean {
  const fields = expression.trim().split(/\s+/)
  return fields.slice(2).some((field) => field !== '*')
}
