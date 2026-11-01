import { describe, it, expect } from 'vitest'
import { countFailedBelow, isPartiallyDone } from '../utilities/partialOutcome'

const job = (status: string | number, children: any[] = []) => ({ status, children })

describe('isPartiallyDone', () => {
  it('is false for a job without children', () => {
    expect(isPartiallyDone(job('Done'))).toBe(false)
  })

  it('is false when every job below it is Done', () => {
    expect(isPartiallyDone(job('Done', [job('Done'), job('Done', [job('DueDone')])]))).toBe(false)
  })

  it('is false when the jobs below it were only skipped: that is by design', () => {
    expect(isPartiallyDone(job('Done', [job('Skipped'), job('Done', [job('Skipped')])]))).toBe(false)
  })

  it.each(['Failed', 'Cancelled', 5, 6])('is true when a child is %s', (status) => {
    expect(isPartiallyDone(job('Done', [job('Done'), job(status)]))).toBe(true)
  })

  it('looks at every depth', () => {
    expect(isPartiallyDone(job('Done', [job('Done', [job('Done', [job('Failed')])])]))).toBe(true)
  })

  it('applies to DueDone and to the numeric statuses', () => {
    expect(isPartiallyDone(job('DueDone', [job('Failed')]))).toBe(true)
    expect(isPartiallyDone(job(3, [job(5)]))).toBe(true)
  })

  it('is false when the job itself did not end Done: it keeps its own status', () => {
    expect(isPartiallyDone(job('Failed', [job('Failed')]))).toBe(false)
    expect(isPartiallyDone(job('InProgress', [job('Failed')]))).toBe(false)
    expect(isPartiallyDone(job('Skipped', [job('Failed')]))).toBe(false)
  })

  it('copes with missing or null children', () => {
    expect(isPartiallyDone({ status: 'Done', children: null })).toBe(false)
    expect(isPartiallyDone({ status: 'Done' })).toBe(false)
  })
})

describe('countFailedBelow', () => {
  it('counts failed and cancelled jobs at every depth, not skipped or done ones', () => {
    const tree = job('Done', [job('Failed'), job('Skipped'), job('Done', [job('Cancelled'), job('Done')])])
    expect(countFailedBelow(tree)).toBe(2)
  })
})
