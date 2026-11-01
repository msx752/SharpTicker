import { describe, it, expect } from 'vitest'
import { priorityNote } from '@/utilities/priorityNote'

describe('priorityNote', () => {
  it('describes what each priority does when jobs are waiting', () => {
    expect(priorityNote('High')).toMatch(/High jobs start first/)
    expect(priorityNote('Normal')).toMatch(/after High and before Low/)
    expect(priorityNote('Low')).toMatch(/after High and Normal/)
  })

  it('describes what LongRunning does', () => {
    expect(priorityNote('LongRunning')).toMatch(/outside the worker pool/)
  })

  it('never says a priority is not applied', () => {
    for (const priority of ['High', 'Normal', 'Low', 'LongRunning']) {
      expect(priorityNote(priority)).not.toMatch(/not applied/i)
    }
  })
})
