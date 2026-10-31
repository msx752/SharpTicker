/**
 * The request the Run again button sends: a new time ticker with the same function, request data, retries, retry
 * intervals and description as the row, and no execution time so the server runs it at once.
 */
import { describe, expect, it } from 'vitest'
import { buildRunAgainRequest } from '@/utilities/runAgain'

const row = (overrides: Partial<Parameters<typeof buildRunAgainRequest>[0]> = {}) => ({
  function: 'WebApiSample_ProcessOrder',
  retries: 2 as string | number,
  retryIntervals: ['30s', '1m 30s'] as string[] | string | null,
  description: 'original order',
  ...overrides,
})

describe('buildRunAgainRequest', () => {
  it('copies the function, retries and description', () => {
    expect(buildRunAgainRequest(row(), '{"OrderId":"A-1"}')).toMatchObject({
      function: 'WebApiSample_ProcessOrder',
      retries: 2,
      description: 'original order',
    })
  })

  it('passes the stored request through exactly as it came from the server', () => {
    const stored = '{"OrderId":"A-1","Amount":10.50,"Big":12345678901234567890}'

    expect(buildRunAgainRequest(row(), stored).request).toBe(stored)
  })

  it.each([null, undefined])('sends an empty request when the function has none (%s)', (stored) => {
    expect(buildRunAgainRequest(row(), stored).request).toBe('')
  })

  it('turns the retry intervals from the list text back into seconds', () => {
    expect(buildRunAgainRequest(row({ retryIntervals: ['30s', '1m 30s', '2h'] }), '').retryIntervals).toEqual([30, 90, 7200])
  })

  it.each([null, '30s'])('sends no intervals when the row has none as a list (%j)', (retryIntervals) => {
    expect(buildRunAgainRequest(row({ retryIntervals }), '').retryIntervals).toBeUndefined()
  })

  it.each([
    ['3', 3],
    [3, 3],
    [0, 0],
    ['', 0],
    ['not a number', 0],
  ])('reads retries %j as %i', (retries, expected) => {
    expect(buildRunAgainRequest(row({ retries }), '').retries).toBe(expected)
  })

  it('sends an empty description when the row has none', () => {
    expect(buildRunAgainRequest(row({ description: undefined as unknown as string }), '').description).toBe('')
  })

  it('has no execution time, so the server runs it right away', () => {
    const request = buildRunAgainRequest(row(), '')

    expect('executionTime' in request).toBe(false)
  })

  it('leaves the row it was built from untouched', () => {
    const item = row()
    const before = JSON.stringify(item)

    buildRunAgainRequest(item, '{}')

    expect(JSON.stringify(item)).toBe(before)
  })
})
