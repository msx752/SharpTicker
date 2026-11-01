/**
 * The Time Tickers and Cron Tickers pages format the dates they receive with the time zone in use at that moment.
 * When a page opened before the scheduler's time zone was known, it loaded once with UTC, then loaded everything again
 * when the zone arrived: the table and the charts were filled and drawn twice. `whenSchedulerTimeZoneKnown` lets the
 * first load wait for the zone, but never for longer than a limit and never when no zone is coming.
 */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createPinia, setActivePinia } from 'pinia'
import { useTimeZoneStore } from '@/stores/timeZoneStore'

describe('time zone store: whenSchedulerTimeZoneKnown', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
    vi.useFakeTimers()
  })
  afterEach(() => {
    vi.useRealTimers()
  })

  const state = (p: Promise<void>) => {
    const s = { done: false }
    void p.then(() => (s.done = true))
    return s
  }

  it('waits until the scheduler time zone is set', async () => {
    const store = useTimeZoneStore()
    const waiting = state(store.whenSchedulerTimeZoneKnown())

    await vi.advanceTimersByTimeAsync(500)
    expect(waiting.done).toBe(false)

    store.setSchedulerTimeZone('Europe/Istanbul')
    await vi.advanceTimersByTimeAsync(0)
    expect(waiting.done).toBe(true)
  })

  it('does not wait when the scheduler time zone is already known', async () => {
    const store = useTimeZoneStore()
    store.setSchedulerTimeZone('UTC')

    const waiting = state(store.whenSchedulerTimeZoneKnown())
    await vi.advanceTimersByTimeAsync(0)

    expect(waiting.done).toBe(true)
  })

  it('does not wait when the user has chosen a time zone, because the zone in use cannot change', async () => {
    const store = useTimeZoneStore()
    store.setSelectedTimeZone('America/New_York')

    const waiting = state(store.whenSchedulerTimeZoneKnown())
    await vi.advanceTimersByTimeAsync(0)

    expect(waiting.done).toBe(true)
  })

  it('stops waiting when the server was asked and sent no zone', async () => {
    const store = useTimeZoneStore()
    const waiting = state(store.whenSchedulerTimeZoneKnown())

    store.markSchedulerTimeZoneSettled()
    await vi.advanceTimersByTimeAsync(0)

    expect(waiting.done).toBe(true)
    expect(store.effectiveTimeZone).toBe('UTC')
  })

  it('gives up after the limit when the zone never arrives, so a page is never blocked for good', async () => {
    const store = useTimeZoneStore()
    const waiting = state(store.whenSchedulerTimeZoneKnown(3000))

    await vi.advanceTimersByTimeAsync(2999)
    expect(waiting.done).toBe(false)

    await vi.advanceTimersByTimeAsync(1)
    expect(waiting.done).toBe(true)
  })
})
