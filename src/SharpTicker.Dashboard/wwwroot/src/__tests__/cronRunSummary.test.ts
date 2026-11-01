import { describe, it, expect } from 'vitest'
import { toRunFields, nextRunText, statusName } from '@/utilities/cronRunSummary'

describe('toRunFields', () => {
  it('formats both times in the zone the dashboard shows', () => {
    const fields = toRunFields(
      { id: 'a', nextRun: '2026-11-01T10:00:00Z', lastRun: '2026-11-01T09:00:00Z', lastStatus: 3, lastOccurrenceId: 'o' },
      'Asia/Tokyo',
    )
    expect(fields.nextRun).toBe('2026-11-01 19:00:00')
    expect(fields.lastRun).toBe('2026-11-01 18:00:00')
    expect(fields.lastStatus).toBe(3)
    expect(fields.lastOccurrenceId).toBe('o')
  })

  it('gives a different text when the zone changes', () => {
    const summary = { id: 'a', nextRun: '2026-11-01T10:00:00Z', lastRun: null, lastStatus: null, lastOccurrenceId: null }
    expect(toRunFields(summary, 'UTC').nextRun).not.toBe(toRunFields(summary, 'Asia/Tokyo').nextRun)
  })

  it('leaves out what the server did not give', () => {
    const fields = toRunFields({ id: 'a', nextRun: null, lastRun: null, lastStatus: null, lastOccurrenceId: null }, 'UTC')
    expect(fields).toEqual({ lastRun: undefined, lastStatus: undefined, lastOccurrenceId: undefined, nextRun: undefined })
  })
})

describe('nextRunText', () => {
  it('says Disabled for a disabled cron, whatever the server sent', () => {
    expect(nextRunText({ isEnabled: false, nextRun: '2026-11-01 10:00:00' })).toBe('Disabled')
  })
  it('shows the time, or a dash while it is not known', () => {
    expect(nextRunText({ isEnabled: true, nextRun: '2026-11-01 10:00:00' })).toBe('2026-11-01 10:00:00')
    expect(nextRunText({ isEnabled: true })).toBe('—')
  })
})

describe('statusName', () => {
  it('names the statuses and gives nothing for an unknown one', () => {
    expect(statusName(5)).toBe('Failed')
    expect(statusName(3)).toBe('Done')
    expect(statusName(null)).toBe('')
    expect(statusName(99)).toBe('')
  })
})
