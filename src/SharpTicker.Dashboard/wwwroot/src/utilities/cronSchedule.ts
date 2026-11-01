import cronstrue from 'cronstrue'
import { isValidCronExpression } from '@/utilities/cronExpression'

/**
 * The schedule of a cron ticker in words, for the Cron Tickers table: "Every 5 minutes", "At 01:00 AM".
 *
 * Returns null when the expression is empty, not valid, or cronstrue cannot put it in words (for example with a full
 * day name); the table then shows the expression alone.
 *
 * The scheduler evaluates an expression in its own time zone, not in the one the dashboard shows times in. The zone is
 * not part of the sentence, which would crowd every row: see {@link scheduleTimeZoneNote}.
 */
export function describeSchedule(expression: string | null | undefined): string | null {
  if (!expression || !isValidCronExpression(expression)) return null

  try {
    return cronstrue.toString(expression)
  } catch {
    return null
  }
}

/**
 * The note for the sentence, shown on hover: which time zone the scheduler reads it in. The zone matters only when the
 * expression is tied to a time of day or to a date; "every 5 minutes" is the same in every zone, so it has no note.
 * Null when there is nothing to say or the zone is not known yet.
 */
export function scheduleTimeZoneNote(
  expression: string | null | undefined,
  schedulerTimeZone: string | null | undefined,
): string | null {
  if (!expression || !schedulerTimeZone || !isValidCronExpression(expression)) return null

  return dependsOnTimeZone(expression) ? `Scheduler time: ${schedulerTimeZone}` : null
}

/** True when the hour, the day of the month, the month or the day of the week is fixed, so the zone decides when it fires. */
export function dependsOnTimeZone(expression: string): boolean {
  const fields = expression.trim().split(/\s+/)
  return fields.slice(2).some((field) => field !== '*')
}
