import { describe, it, expect } from 'vitest'
import { ActionFailedError, getActionFailure } from '@/http/base/actionResponse'

describe('getActionFailure', () => {
  it('returns the message of a refused action', () => {
    expect(getActionFailure({ success: false, message: 'Failed to add cron ticker: bad cron' })).toBe(
      'Failed to add cron ticker: bad cron',
    )
  })

  it('falls back to a generic message when the refusal has none', () => {
    expect(getActionFailure({ success: false })).toBe('The request was refused.')
    expect(getActionFailure({ success: false, message: '' })).toBe('The request was refused.')
    expect(getActionFailure({ success: false, message: 42 })).toBe('The request was refused.')
  })

  it('is undefined for a successful action', () => {
    expect(getActionFailure({ success: true, message: 'Cron ticker added successfully' })).toBeUndefined()
  })

  it('is undefined for data that is not an action response', () => {
    expect(getActionFailure(undefined)).toBeUndefined()
    expect(getActionFailure(null)).toBeUndefined()
    expect(getActionFailure('text')).toBeUndefined()
    expect(getActionFailure([])).toBeUndefined()
    expect(getActionFailure({ id: 1, status: 3 })).toBeUndefined()
    expect(getActionFailure({ success: 'false' })).toBeUndefined()
  })
})

describe('ActionFailedError', () => {
  it('is an Error that carries the message and can be told apart', () => {
    const error = new ActionFailedError('refused')

    expect(error).toBeInstanceOf(Error)
    expect(error.message).toBe('refused')
    expect(error.name).toBe('ActionFailedError')
  })
})
