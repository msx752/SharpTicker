import { describe, it, expect } from 'vitest'
import { priorityNote, isPriorityApplied } from '@/utilities/priorityNote'

describe('priorityNote', () => {
  it('says High, Normal and Low are not applied yet', () => {
    for (const priority of ['High', 'Normal', 'Low']) {
      expect(isPriorityApplied(priority)).toBe(false)
      expect(priorityNote(priority)).toMatch(/not applied yet/i)
    }
  })

  it('describes what LongRunning does', () => {
    expect(isPriorityApplied('LongRunning')).toBe(true)
    expect(priorityNote('LongRunning')).toMatch(/outside the worker pool/)
  })
})
