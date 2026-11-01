/**
 * A page reloads its data when the time zone it shows dates in changes, because the dates are formatted when they
 * arrive. It must not do that for the change that merely tells it which zone its own first load already uses: that
 * made every page open load and draw everything twice.
 */
import { describe, expect, it } from 'vitest'
import { createZoneReloadGate } from '@/utilities/zoneReloadGate'

function setup(initial = 'UTC') {
  let zone = initial
  let release!: () => void
  const known = new Promise<void>((r) => (release = r))
  const gate = createZoneReloadGate(() => known, () => zone)
  return {
    gate,
    setZone: (z: string) => (zone = z),
    /** The zone becomes known: the first load may go on. */
    release,
  }
}

describe('createZoneReloadGate', () => {
  it('ignores a zone change while the first load is still waiting for the zone', () => {
    const { gate, setZone } = setup()

    setZone('Europe/Istanbul')

    expect(gate.shouldReload('Europe/Istanbul')).toBe(false)
  })

  it('ignores the change that sets the zone the first load then uses', async () => {
    const { gate, setZone, release } = setup()
    const step = gate.firstLoad(async () => 'loaded')

    const running = step()
    setZone('Europe/Istanbul')
    // The store tells the watcher first, then the waiting load goes on
    expect(gate.shouldReload('Europe/Istanbul')).toBe(false)
    release()
    await running

    // A later report of the same zone is not a change either
    expect(gate.shouldReload('Europe/Istanbul')).toBe(false)
  })

  it('runs the load with the zone that is in use when the wait ends', async () => {
    const { gate, setZone, release } = setup()
    let usedZone = ''
    const step = gate.firstLoad(async () => {
      usedZone = 'seen'
    })

    const running = step()
    setZone('Asia/Tokyo')
    release()
    await running

    expect(usedZone).toBe('seen')
    expect(gate.shouldReload('Asia/Tokyo')).toBe(false)
  })

  it('reloads when the user picks another zone after the first load', async () => {
    const { gate, release } = setup('Europe/Istanbul')
    release()
    await gate.firstLoad(async () => undefined)()

    expect(gate.shouldReload('America/New_York')).toBe(true)
  })

  it('reloads when the zone arrives after the wait gave up, because the first load used UTC', async () => {
    const { gate, setZone, release } = setup('UTC')
    release() // the limit passed: the load goes on with the zone in use, UTC
    await gate.firstLoad(async () => undefined)()

    setZone('Europe/Istanbul')

    expect(gate.shouldReload('Europe/Istanbul')).toBe(true)
  })

  it('follows the zone of the latest load, so switching away and back reloads each time', async () => {
    const { gate, release } = setup('Europe/Istanbul')
    release()
    await gate.firstLoad(async () => undefined)()

    expect(gate.shouldReload('UTC')).toBe(true)
    expect(gate.shouldReload('Europe/Istanbul')).toBe(true)
    expect(gate.shouldReload('Europe/Istanbul')).toBe(false)
  })

  it('gives each wrapped step the result of its load', async () => {
    const { gate, release } = setup()
    release()

    await expect(gate.firstLoad(async () => 42)()).resolves.toBe(42)
  })
})
