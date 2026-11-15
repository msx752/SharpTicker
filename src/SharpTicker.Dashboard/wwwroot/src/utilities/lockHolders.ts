import { relativeTime } from '@/utilities/relativeTime'

/**
 * One entry of `/ticker/machine/jobs`: a node identifier that locked jobs still in the store. A job keeps that name after
 * it ended, so the name of a node that is gone stays in the list. `hasHeartbeat` is only known with Redis.
 */
export interface LockHolderEntry {
  item1: string
  item2: number
  lastLockedAt?: string | null
  hasHeartbeat?: boolean | null
}

export interface LockHolderRow {
  machine: string
  locked: string
}

/** The line under a name: its jobs, its latest lock, and with Redis whether its heartbeat is there. */
export function toLockHolderRow(entry: LockHolderEntry, now: number = Date.now()): LockHolderRow {
  const parts = [`${entry.item2} ${entry.item2 === 1 ? 'job' : 'jobs'}`]
  const last = relativeTime(entry.lastLockedAt, now)
  if (last) parts.push(`last lock ${last}`)
  if (entry.hasHeartbeat === true) parts.push('heartbeat')
  if (entry.hasHeartbeat === false) parts.push('no heartbeat')
  return { machine: entry.item1, locked: parts.join(', ') }
}

/** The subtitle of the panel: what the names are, and how many have a heartbeat when that is known. */
export function lockHoldersSubtitle(entries: LockHolderEntry[]): string {
  const names = `${entries.length} ${entries.length === 1 ? 'name' : 'names'} that locked jobs, latest lock first`
  if (!entries.some((e) => e.hasHeartbeat === true || e.hasHeartbeat === false)) return names
  const alive = entries.filter((e) => e.hasHeartbeat === true).length
  return `${names}, ${alive} with a heartbeat`
}
