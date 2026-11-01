/**
 * The schedule of a cron ticker put in words for the Cron Tickers table. The scheduler evaluates an expression in its
 * own time zone, not in the one the dashboard shows times in, so the sentence names that zone.
 */
import { describe, expect, it } from 'vitest'
import { describeSchedule } from '@/utilities/cronSchedule'

describe('describeSchedule', () => {
  it('puts the expression in words and names the scheduler time zone', () => {
    expect(describeSchedule('0 0 1 * * *', 'UTC')).toBe('At 01:00 AM, UTC')
  })

  it('leaves the zone out when it is not known yet', () => {
    expect(describeSchedule('0 0 1 * * *', null)).toBe('At 01:00 AM')
    expect(describeSchedule('0 0 1 * * *', undefined)).toBe('At 01:00 AM')
    expect(describeSchedule('0 0 1 * * *', '')).toBe('At 01:00 AM')
  })

  it('uses the expression with seconds first, as the scheduler does', () => {
    expect(describeSchedule('30 15 10 * * *', 'Europe/Istanbul')).toBe('At 10:15:30 AM, Europe/Istanbul')
  })

  it('returns null for an expression it cannot describe, so the table shows only the expression', () => {
    expect(describeSchedule('not a cron', 'UTC')).toBeNull()
    expect(describeSchedule('', 'UTC')).toBeNull()
    expect(describeSchedule(null, 'UTC')).toBeNull()
    expect(describeSchedule(undefined, 'UTC')).toBeNull()
  })
})
