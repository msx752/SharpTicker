import { describe, it, expect } from 'vitest'
import {
  noTimeChoice,
  noTimeLabel,
  isStoredChild,
  runConditionLabel,
  EXECUTE_IMMEDIATELY_LABEL,
  STARTED_BY_PARENT_LABEL,
} from '@/utilities/childEdit'

const parentId = '11111111-1111-1111-1111-111111111111'
const id = '22222222-2222-2222-2222-222222222222'

describe('what the dialog offers in place of a time', () => {
  it('lets a stored child without a time stay without one', () => {
    const choice = noTimeChoice({ id, isFromDuplicate: false, parentId, executionTime: null })
    expect(choice).toBe('startedByParent')
    expect(noTimeLabel(choice)).toBe(STARTED_BY_PARENT_LABEL)
  })

  it('requires a time for a stored child that has one', () => {
    const choice = noTimeChoice({ id, isFromDuplicate: false, parentId, executionTime: '2026-01-01T10:00:00Z' })
    expect(choice).toBe('none')
    expect(noTimeLabel(choice)).toBeUndefined()
  })

  it('requires a time for a stored ticker that is not a child', () => {
    expect(noTimeChoice({ id, isFromDuplicate: false, parentId: null, executionTime: '2026-01-01T10:00:00Z' })).toBe('none')
    expect(noTimeChoice({ id, isFromDuplicate: false, executionTime: null })).toBe('none')
  })

  it('offers to run at once for a new ticker', () => {
    const choice = noTimeChoice({ isFromDuplicate: false })
    expect(choice).toBe('immediately')
    expect(noTimeLabel(choice)).toBe(EXECUTE_IMMEDIATELY_LABEL)
  })

  it('offers to run at once for a copy, also the copy of a child without a time', () => {
    expect(noTimeChoice({ id, isFromDuplicate: true, parentId, executionTime: null })).toBe('immediately')
  })
})

describe('stored child', () => {
  it('is a row that is edited and has a parent', () => {
    expect(isStoredChild({ id, isFromDuplicate: false, parentId })).toBe(true)
    expect(isStoredChild({ id, isFromDuplicate: false, parentId: null })).toBe(false)
    expect(isStoredChild({ id, isFromDuplicate: true, parentId })).toBe(false)
    expect(isStoredChild({ isFromDuplicate: false, parentId })).toBe(false)
  })
})

describe('run condition as text', () => {
  it('reads the number the API sends', () => {
    expect(runConditionLabel(0)).toBe('On Success')
    expect(runConditionLabel(3)).toBe('On Failure or Cancelled')
    expect(runConditionLabel(5)).toBe('In Progress (Parallel)')
  })

  it('reads the name', () => {
    expect(runConditionLabel('OnAnyCompletedStatus')).toBe('On Any Completed Status')
  })

  it('has no text for a row without a run condition', () => {
    expect(runConditionLabel(null)).toBeUndefined()
    expect(runConditionLabel(undefined)).toBeUndefined()
  })

  it('shows a value it does not know as it is', () => {
    expect(runConditionLabel(9)).toBe('9')
    expect(runConditionLabel('Later')).toBe('Later')
  })
})
