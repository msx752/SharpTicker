import cronstrue from 'cronstrue'
import { isValidCronExpression } from '@/utilities/cronExpression'

/**
 * The schedule of a cron ticker in words, for the Cron Tickers table: "At 01:00 AM, UTC".
 *
 * The scheduler evaluates an expression in its own time zone, not in the one the dashboard shows times in, so the
 * sentence names the scheduler zone. Returns null when the expression is empty, not valid, or cronstrue cannot put it
 * in words (for example with a full day name); the table then shows the expression alone.
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

  return schedulerTimeZone ? `${sentence}, ${schedulerTimeZone}` : sentence
}
