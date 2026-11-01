/**
 * The schedule of a cron ticker put in words for the Cron Tickers table. The scheduler evaluates an expression in its
 * own time zone, not in the one the dashboard shows times in. The sentence does not name that zone (it would crowd every
 * row); a note for the hover does, for the expressions that depend on it.
 */
import { describe, expect, it } from 'vitest'
import { describeSchedule, scheduleTimeZoneNote } from '@/utilities/cronSchedule'

describe('describeSchedule', () => {
  it('puts the expression in words, without a time zone', () => {
    expect(describeSchedule('0 0 1 * * *')).toBe('At 01:00 AM')
    expect(describeSchedule('0 0 9 * * SUN')).toBe('At 09:00 AM, only on Sunday')
    expect(describeSchedule('0 */5 * * * *')).toBe('Every 5 minutes')
    expect(describeSchedule('0 0 * * * *')).toBe('Every hour')
  })

  it('uses the expression with seconds first, as the scheduler does', () => {
    expect(describeSchedule('30 15 10 * * *')).toBe('At 10:15:30 AM')
  })

  it('returns null for an expression it cannot describe, so the table shows only the expression', () => {
    expect(describeSchedule('not a cron')).toBeNull()
    expect(describeSchedule('')).toBeNull()
    expect(describeSchedule(null)).toBeNull()
    expect(describeSchedule(undefined)).toBeNull()
  })
})

describe('scheduleTimeZoneNote', () => {
  it('names the scheduler time zone when the time of day matters', () => {
    expect(scheduleTimeZoneNote('0 0 1 * * *', 'UTC')).toBe('Scheduler time: UTC')
    expect(scheduleTimeZoneNote('0 0 9 * * SUN', 'Europe/London')).toBe('Scheduler time: Europe/London')
  })

  it('has no note for a schedule that reads the same in every zone', () => {
    expect(scheduleTimeZoneNote('0 */5 * * * *', 'Europe/Istanbul')).toBeNull()
    expect(scheduleTimeZoneNote('0 0 * * * *', 'Europe/Istanbul')).toBeNull()
  })

  it('names the zone when a day or a month is fixed, even if the hour is not', () => {
    expect(scheduleTimeZoneNote('0 0 * * * MON', 'UTC')).toBe('Scheduler time: UTC')
    expect(scheduleTimeZoneNote('0 0 * 1 * *', 'UTC')).toBe('Scheduler time: UTC')
  })

  it('has no note when the zone is not known yet or the expression cannot be read', () => {
    expect(scheduleTimeZoneNote('0 0 1 * * *', null)).toBeNull()
    expect(scheduleTimeZoneNote('0 0 1 * * *', undefined)).toBeNull()
    expect(scheduleTimeZoneNote('0 0 1 * * *', '')).toBeNull()
    expect(scheduleTimeZoneNote('not a cron', 'UTC')).toBeNull()
    expect(scheduleTimeZoneNote(null, 'UTC')).toBeNull()
  })
})
