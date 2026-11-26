/**
 * The chain builder sends the typed time as a wall-clock time for the scheduler's zone, without converting it with
 * the browser's zone: 23:00 typed on 1 December is "2026-12-01T23:00:00" whatever zone the browser is in.
 */
import { describe, expect, it } from 'vitest'
import { toWallClockText } from '@/utilities/schedulerTime'

describe('toWallClockText', () => {
  it('keeps the typed day and time, with no zone and no conversion', () => {
    // A date picker gives the picked day at local midnight.
    expect(toWallClockText(new Date(2026, 11, 1), '23:00:00')).toBe('2026-12-01T23:00:00')
    expect(toWallClockText(new Date(2026, 11, 1), '00:30')).toBe('2026-12-01T00:30:00')
  })

  it('does not move a late time to the next day, or an early one to the day before', () => {
    expect(toWallClockText(new Date(2026, 0, 31), '23:59:59')).toBe('2026-01-31T23:59:59')
    expect(toWallClockText(new Date(2026, 0, 31), '00:00:00')).toBe('2026-01-31T00:00:00')
  })

  it('returns nothing when the day or the time is missing', () => {
    expect(toWallClockText(null, '10:00:00')).toBeUndefined()
    expect(toWallClockText(new Date(2026, 11, 1), '')).toBeUndefined()
    expect(toWallClockText('not a date', '10:00:00')).toBeUndefined()
  })
})
