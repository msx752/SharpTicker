import { defineStore } from 'pinia'
import { ref, computed, watch } from 'vue'

export const useTimeZoneStore = defineStore('timeZone', () => {
  const STORAGE_KEY = 'sharpticker:dashboard:timezone'

  const schedulerTimeZone = ref<string | null>(null)
  const selectedTimeZone = ref<string | null>(null)

  // A small curated list of common IANA time zones.
  // The scheduler's configured time zone will be added dynamically if it's not in this list.
  const commonTimeZones = ref<string[]>([
    'UTC',
    'Europe/London',
    'Europe/Berlin',
    'America/New_York',
    'America/Chicago',
    'America/Denver',
    'America/Los_Angeles',
    'Asia/Tokyo',
    'Asia/Singapore',
    'Australia/Sydney'
  ])

  const availableTimeZones = computed(() => {
    const zones = new Set(commonTimeZones.value)

    if (schedulerTimeZone.value) {
      zones.add(schedulerTimeZone.value)
    }

    // Browser local time zone (if available)
    try {
      const browserTz = Intl.DateTimeFormat().resolvedOptions().timeZone
      if (browserTz) {
        zones.add(browserTz)
      }
    } catch {
      // ignore
    }

    return Array.from(zones)
  })

  const effectiveTimeZone = computed(() => {
    return selectedTimeZone.value || schedulerTimeZone.value || 'UTC'
  })

  // Initialize from localStorage (if available)
  if (typeof window !== 'undefined' && typeof window.localStorage !== 'undefined') {
    const stored = window.localStorage.getItem(STORAGE_KEY)
    if (stored) {
      selectedTimeZone.value = stored
    }
  }

  // True once the dashboard has asked the server for its options, whether or not a zone came back
  const schedulerTimeZoneSettled = ref(false)
  const settledWaiters: Array<() => void> = []

  function markSchedulerTimeZoneSettled() {
    schedulerTimeZoneSettled.value = true
    settledWaiters.splice(0).forEach((resolve) => resolve())
  }

  function setSchedulerTimeZone(id: string | null | undefined) {
    schedulerTimeZone.value = id || null
    markSchedulerTimeZoneSettled()
  }

  /**
   * Resolves when the zone the dates are shown in is final: the scheduler's zone has been read, the server has said it
   * has none, or the user has chosen a zone (then the scheduler's cannot change what is shown).
   *
   * A page that loads its data before this is true gets dates formatted in UTC and has to load again when the zone
   * arrives. It never waits longer than `timeoutMs`, so a slow or failing options request cannot block a page.
   */
  function whenSchedulerTimeZoneKnown(timeoutMs = 3000): Promise<void> {
    if (selectedTimeZone.value || schedulerTimeZoneSettled.value) return Promise.resolve()

    return new Promise<void>((resolve) => {
      const timer = setTimeout(resolve, timeoutMs)
      settledWaiters.push(() => {
        clearTimeout(timer)
        resolve()
      })
    })
  }

  function setSelectedTimeZone(id: string | null) {
    selectedTimeZone.value = id
  }

  // Persist user selection to localStorage
  watch(
    selectedTimeZone,
    (val) => {
      if (typeof window === 'undefined' || typeof window.localStorage === 'undefined') {
        return
      }

      if (val) {
        window.localStorage.setItem(STORAGE_KEY, val)
      } else {
        window.localStorage.removeItem(STORAGE_KEY)
      }
    },
    { immediate: false }
  )

  return {
    schedulerTimeZone,
    selectedTimeZone,
    availableTimeZones,
    effectiveTimeZone,
    setSchedulerTimeZone,
    setSelectedTimeZone,
    markSchedulerTimeZoneSettled,
    whenSchedulerTimeZoneKnown
  }
})
