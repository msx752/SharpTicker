/**
 * The numbers of the dashboard "Job Statistics (Past 7 Days)" section.
 *
 * They all come from one answer of the server (`/ticker/statuses/get-last-week`), so the section has one scope.
 * The entries are told apart by their number: 0 Done and DueDone, 1 Failed, 2 every status, 3 Cancelled.
 */
export interface JobStatisticsEntry {
  item1: number | string
  item2: number
}

export interface JobStatistics {
  successful: number
  failed: number
  cancelled: number
  /** Done, DueDone, Failed and Cancelled: the jobs that have an outcome. The waiting and running ones are not in it. */
  finalJobs: number
  /** Percent with at most one decimal, or null when no job has finished yet. */
  successRate: string | null
}

const DONE_AND_DUE_DONE = 0
const FAILED = 1
const CANCELLED = 3

export function summarizeJobStatuses(entries: JobStatisticsEntry[] | undefined): JobStatistics | null {
  if (!entries) return null

  const count = (key: number) =>
    entries.filter((e) => Number(e.item1) === key).reduce((sum, e) => sum + e.item2, 0)

  const successful = count(DONE_AND_DUE_DONE)
  const failed = count(FAILED)
  const cancelled = count(CANCELLED)
  const finalJobs = successful + failed + cancelled

  return { successful, failed, cancelled, finalJobs, successRate: formatSuccessRate(successful, finalJobs) }
}

/**
 * Rounds down, so a rate below 100 is never shown as 100: 141343 of 141394 reads 99.9, not 100.
 */
function formatSuccessRate(successful: number, finalJobs: number): string | null {
  if (finalJobs <= 0) return null
  if (successful >= finalJobs) return '100'

  // successful * 1000 is an exact integer, so a rate such as 29% is not pushed to 28.9 by floating point error
  const oneDecimal = (Math.floor((successful * 1000) / finalJobs) / 10).toFixed(1)
  return oneDecimal.endsWith('.0') ? oneDecimal.slice(0, -2) : oneDecimal
}

/**
 * The share of one status among all jobs, for the Status Distribution panel: one decimal, no trailing ".0".
 * A status that has jobs but a share below the rounding step reads "<0.1", not "0": one failed job among 2001 must not
 * disappear from the panel.
 */
export function formatShare(count: number, total: number): string {
  if (count <= 0 || total <= 0) return '0'

  const share = (count / total) * 100
  if (share < 0.05) return '<0.1'

  const oneDecimal = share.toFixed(1)
  return oneDecimal.endsWith('.0') ? oneDecimal.slice(0, -2) : oneDecimal
}
