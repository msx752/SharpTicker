/**
 * The dashboard "Job Statistics (Past 7 Days)" numbers. Every number comes from the past-week answer of the server
 * (0 Done and DueDone, 1 Failed, 2 every status, 3 Cancelled), so the section has one scope, and a success rate
 * below 100% is never shown as 100%.
 */
import { describe, expect, it } from 'vitest'
import { summarizeJobStatuses, type JobStatisticsEntry } from '@/utilities/jobStatistics'

const week = (done: number, failed: number, total: number, cancelled: number) => [
  { item1: 0, item2: done },
  { item1: 1, item2: failed },
  { item1: 2, item2: total },
  { item1: 3, item2: cancelled },
]

describe('summarizeJobStatuses', () => {
  it('reads every number from the same answer', () => {
    const s = summarizeJobStatuses(week(90, 4, 120, 1))

    expect(s.successful).toBe(90)
    expect(s.failed).toBe(4)
    expect(s.cancelled).toBe(1)
  })

  it('counts Done, DueDone, Failed and Cancelled as the final jobs, not the waiting ones', () => {
    const s = summarizeJobStatuses(week(90, 4, 120, 1))

    expect(s.finalJobs).toBe(95)
  })

  it('finds an entry by its number, not by its position', () => {
    const s = summarizeJobStatuses([
      { item1: 3, item2: 7 },
      { item1: 1, item2: 2 },
      { item1: 0, item2: 11 },
    ])

    expect(s.successful).toBe(11)
    expect(s.failed).toBe(2)
    expect(s.cancelled).toBe(7)
  })

  it('accepts the entry number as text, as the response type declares it', () => {
    const entries: JobStatisticsEntry[] = [
      { item1: '0', item2: 5 },
      { item1: '1', item2: 1 },
    ]
    const s = summarizeJobStatuses(entries)

    expect(s.successful).toBe(5)
    expect(s.failed).toBe(1)
  })

  it('shows 100 only when nothing failed or was cancelled', () => {
    expect(summarizeJobStatuses(week(50, 0, 50, 0)).successRate).toBe('100')
  })

  it('never rounds a rate below 100 up to 100', () => {
    // 141343 of 141394 is 99.96%: it must read 99.9, not 100
    expect(summarizeJobStatuses(week(141343, 50, 141394, 1)).successRate).toBe('99.9')
    expect(summarizeJobStatuses(week(999999, 1, 1000000, 0)).successRate).toBe('99.9')
  })

  it('shows one decimal and drops a trailing zero', () => {
    expect(summarizeJobStatuses(week(1, 1, 2, 0)).successRate).toBe('50')
    expect(summarizeJobStatuses(week(97, 3, 100, 0)).successRate).toBe('97')
    expect(summarizeJobStatuses(week(2, 1, 3, 0)).successRate).toBe('66.6')
  })

  it('has no rate when no job has finished', () => {
    expect(summarizeJobStatuses(week(0, 0, 5, 0)).successRate).toBeNull()
  })

  it('treats a missing entry as zero, for example an older server without Cancelled', () => {
    const s = summarizeJobStatuses([
      { item1: 0, item2: 8 },
      { item1: 1, item2: 2 },
      { item1: 2, item2: 12 },
    ])

    expect(s.cancelled).toBe(0)
    expect(s.finalJobs).toBe(10)
    expect(s.successRate).toBe('80')
  })

  it('has no numbers before the answer arrived', () => {
    expect(summarizeJobStatuses(undefined)).toBeNull()
  })
})
