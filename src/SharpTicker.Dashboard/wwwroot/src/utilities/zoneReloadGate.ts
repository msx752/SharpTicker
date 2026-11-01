/**
 * Decides when a page reloads its data because the dashboard time zone changed.
 *
 * The dates a page shows are formatted when they arrive, with the zone in use at that moment, so a change of zone means
 * a reload. But the first load of a page waits for the scheduler's zone to be known (`whenSchedulerTimeZoneKnown`), and
 * the change that makes it known is the very change the page is watching. Without this gate that change started a second
 * load next to the first one: on every page open the table and the charts were filled and drawn twice.
 *
 * - While the first load waits for the zone, a change is ignored: the first load will use it.
 * - A change to the zone the latest load already used is ignored.
 * - Anything else (the user picks another zone, or the zone arrives after the wait gave up) reloads.
 */
export interface ZoneReloadGate {
  /** Wraps a step of the first load: it waits for the zone, notes which zone the load uses, then runs. */
  firstLoad: <T>(load: () => Promise<T>) => () => Promise<T>
  /** Call it from the zone watcher. True when a reload is needed; the zone is then noted as the one of the latest load. */
  shouldReload: (zone: string) => boolean
}

export function createZoneReloadGate(
  whenZoneKnown: () => Promise<void>,
  currentZone: () => string,
): ZoneReloadGate {
  let waiting = true
  let zoneOfLatestLoad: string | null = null

  return {
    firstLoad: (load) => async () => {
      await whenZoneKnown()
      waiting = false
      zoneOfLatestLoad = currentZone()
      return load()
    },

    shouldReload: (zone) => {
      if (waiting || zone === zoneOfLatestLoad) return false
      zoneOfLatestLoad = zone
      return true
    },
  }
}
