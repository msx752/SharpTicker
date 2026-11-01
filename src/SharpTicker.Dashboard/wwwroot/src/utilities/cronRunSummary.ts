import { formatDate } from '@/utilities/dateTimeParser'
import { relativeTime } from '@/utilities/relativeTime'

/** What the server answers for one cron: see `CronRunSummary` in the dashboard endpoints. */
export interface CronRunSummaryResponse {
  id: string
  nextRun: string | null
  lastRun: string | null
  lastStatus: number | null
  lastOccurrenceId: string | null
}

/** The fields the Cron Tickers table adds to a row once the summary has arrived. */
export interface CronRunFields {
  /** The moments as the server sent them (UTC), for the words relative to now. */
  lastRunAt?: string
  nextRunAt?: string
  /** The same moments formatted in the zone the dashboard shows, for the tooltip. */
  lastRun?: string
  lastStatus?: number
  lastOccurrenceId?: string
  nextRun?: string
}

// The numbers are TickerStatus, as in the Status enum; they are written out so this file does not depend on that module
const STATUS_NAMES: Record<number, string> = {
  0: 'Idle',
  1: 'Queued',
  2: 'In progress',
  3: 'Done',
  4: 'Done (late)',
  5: 'Failed',
  6: 'Cancelled',
  7: 'Skipped',
}

export const statusName = (status: number | null | undefined): string =>
  status == null ? '' : (STATUS_NAMES[status] ?? '')

/** The times are formatted in the zone the dashboard shows, so a change of that zone needs a new call. */
export function toRunFields(summary: CronRunSummaryResponse, timeZone: string): CronRunFields {
  return {
    lastRunAt: summary.lastRun ?? undefined,
    nextRunAt: summary.nextRun ?? undefined,
    lastRun: summary.lastRun ? formatDate(summary.lastRun, true, timeZone) : undefined,
    lastStatus: summary.lastStatus ?? undefined,
    lastOccurrenceId: summary.lastOccurrenceId ?? undefined,
    nextRun: summary.nextRun ? formatDate(summary.nextRun, true, timeZone) : undefined,
  }
}

/** The text of the Next Run cell: how far ahead it is in words, or why there is none. */
export function nextRunText(row: { isEnabled: boolean; nextRunAt?: string }, now: number = Date.now()): string {
  if (!row.isEnabled) return 'Disabled'
  return relativeTime(row.nextRunAt, now) || '—'
}
