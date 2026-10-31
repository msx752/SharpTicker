/**
 * The debounce helper the dashboard uses so a burst of events, such as many live updates in a row, causes one reload:
 * the call runs once after the burst and receives the latest arguments.
 */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { debounce } from '@/utilities/debounce'

describe('debounce', () => {
  beforeEach(() => vi.useFakeTimers())
  afterEach(() => vi.useRealTimers())

  it('runs once after a burst of calls', () => {
    const fn = vi.fn()
    const run = debounce(fn, 300)

    run()
    run()
    run()
    vi.advanceTimersByTime(299)
    expect(fn).not.toHaveBeenCalled()
    vi.advanceTimersByTime(1)
    expect(fn).toHaveBeenCalledTimes(1)
  })

  it('passes the latest arguments', () => {
    const fn = vi.fn()
    const run = debounce(fn, 100)

    run('a')
    run('b')
    vi.advanceTimersByTime(100)

    expect(fn).toHaveBeenCalledWith('b')
  })
})
