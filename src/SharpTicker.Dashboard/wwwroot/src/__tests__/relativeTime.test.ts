import { describe, it, expect } from 'vitest'
import { relativeTime } from '@/utilities/relativeTime'

const now = Date.parse('2026-11-01T12:00:00Z')
const at = (ms: number) => new Date(now + ms).toISOString()
const MIN = 60_000, HOUR = 3_600_000, DAY = 86_400_000

describe('relativeTime', () => {
  it('says how far ahead a moment is, in days, hours and minutes', () => {
    expect(relativeTime(at(4 * MIN), now)).toBe('in 4m')
    expect(relativeTime(at(3 * HOUR + 5 * MIN), now)).toBe('in 3h 5m')
    expect(relativeTime(at(3 * DAY + 2 * HOUR + MIN), now)).toBe('in 3d 2h 1m')
  })

  it('says how long ago a moment was', () => {
    expect(relativeTime(at(-5 * MIN), now)).toBe('5m ago')
    expect(relativeTime(at(-(2 * HOUR + 30 * MIN)), now)).toBe('2h 30m ago')
    expect(relativeTime(at(-(3 * DAY + 2 * HOUR + MIN)), now)).toBe('3d 2h 1m ago')
  })

  it('leaves out a unit that is zero', () => {
    expect(relativeTime(at(DAY + 5 * MIN), now)).toBe('in 1d 5m')
    expect(relativeTime(at(2 * HOUR), now)).toBe('in 2h')
  })

  it('shows seconds only when less than a minute is left, and does not round up', () => {
    expect(relativeTime(at(30_000), now)).toBe('in 30s')
    expect(relativeTime(at(-45_000), now)).toBe('45s ago')
    expect(relativeTime(at(59_900), now)).toBe('in 59s')
    expect(relativeTime(at(MIN + 59_000), now)).toBe('in 1m')
  })

  it('says now for less than a second, and nothing for a missing or invalid value', () => {
    expect(relativeTime(at(400), now)).toBe('now')
    expect(relativeTime(null, now)).toBe('')
    expect(relativeTime('', now)).toBe('')
    expect(relativeTime('not a date', now)).toBe('')
  })

  it('reads a time without a Z as UTC, as the dashboard dates are', () => {
    expect(relativeTime('2026-11-01 12:10:00', now)).toBe('in 10m')
  })
})
