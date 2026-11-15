import { describe, it, expect } from 'vitest'
import { lockHoldersSubtitle, toLockHolderRow } from '@/utilities/lockHolders'

const now = Date.parse('2026-11-01T12:00:00Z')
const DAY = 86_400_000

describe('toLockHolderRow', () => {
  it('shows the jobs of a name and how long ago it last locked one', () => {
    const row = toLockHolderRow({ item1: 'container-gone', item2: 2, lastLockedAt: new Date(now - 30 * DAY).toISOString() }, now)
    expect(row).toEqual({ machine: 'container-gone', locked: '2 jobs, last lock 30d ago' })
  })

  it('says whether the heartbeat is there when the answer knows it', () => {
    const at = new Date(now - 60_000).toISOString()
    expect(toLockHolderRow({ item1: 'a', item2: 1, lastLockedAt: at, hasHeartbeat: true }, now).locked).toBe('1 job, last lock 1m ago, heartbeat')
    expect(toLockHolderRow({ item1: 'b', item2: 1, lastLockedAt: at, hasHeartbeat: false }, now).locked).toBe('1 job, last lock 1m ago, no heartbeat')
    expect(toLockHolderRow({ item1: 'c', item2: 1, lastLockedAt: at, hasHeartbeat: null }, now).locked).toBe('1 job, last lock 1m ago')
  })

  it('reads an answer of 1.5.2 or earlier, which has only the name and the count', () => {
    expect(toLockHolderRow({ item1: 'old', item2: 3 }, now).locked).toBe('3 jobs')
  })
})

describe('lockHoldersSubtitle', () => {
  it('calls the entries names that locked jobs, not machines', () => {
    const subtitle = lockHoldersSubtitle([{ item1: 'a', item2: 1 }, { item1: 'b', item2: 1 }])
    expect(subtitle).toBe('2 names that locked jobs, latest lock first')
    expect(subtitle).not.toMatch(/machine/i)
  })

  it('counts the names with a heartbeat when that is known', () => {
    expect(lockHoldersSubtitle([
      { item1: 'a', item2: 1, hasHeartbeat: true },
      { item1: 'b', item2: 1, hasHeartbeat: false },
    ])).toBe('2 names that locked jobs, latest lock first, 1 with a heartbeat')
  })
})
