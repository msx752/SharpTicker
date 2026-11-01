/**
 * The schedule of a cron ticker put in words for the Cron Tickers table. The scheduler evaluates an expression in its
 * own time zone, not in the one the dashboard shows times in, so a sentence tied to a time of day or a date names that
 * zone; one that reads the same in every zone ("Every 5 minutes") does not.
 */
import { describe, expect, it } from 'vitest'
import { describeSchedule } from '@/utilities/cronSchedule'

describe('describeSchedule', () => {
  it('puts the expression in words and names the scheduler time zone when the time of day matters', () => {
    expect(describeSchedule('0 0 1 * * *', 'UTC')).toBe('At 01:00 AM (scheduler time, UTC)')
  })

  it('does not name a zone for a schedule that reads the same in every zone', () => {
    expect(describeSchedule('0 */5 * * * *', 'Europe/Istanbul')).toBe('Every 5 minutes')
    expect(describeSchedule('0 0 * * * *', 'Europe/Istanbul')).toBe('Every hour')
  })

  it('names the zone when a day or a month is fixed, even if the hour is not', () => {
    expect(describeSchedule('0 0 * * * MON', 'UTC')).toContain('(scheduler time, UTC)')
    expect(describeSchedule('0 0 * 1 * *', 'UTC')).toContain('(scheduler time, UTC)')
  })

  it('leaves the zone out when it is not known yet', () => {
    expect(describeSchedule('0 0 1 * * *', null)).toBe('At 01:00 AM')
    expect(describeSchedule('0 0 1 * * *', undefined)).toBe('At 01:00 AM')
    expect(describeSchedule('0 0 1 * * *', '')).toBe('At 01:00 AM')
  })

  it('uses the expression with seconds first, as the scheduler does', () => {
    expect(describeSchedule('30 15 10 * * *', 'Europe/Istanbul')).toBe('At 10:15:30 AM (scheduler time, Europe/Istanbul)')
  })

  it('returns null for an expression it cannot describe, so the table shows only the expression', () => {
    expect(describeSchedule('not a cron', 'UTC')).toBeNull()
    expect(describeSchedule('', 'UTC')).toBeNull()
    expect(describeSchedule(null, 'UTC')).toBeNull()
    expect(describeSchedule(undefined, 'UTC')).toBeNull()
  })
})
