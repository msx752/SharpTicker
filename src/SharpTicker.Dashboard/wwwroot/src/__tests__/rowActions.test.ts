import { describe, it, expect } from 'vitest'
import {
  timeTickerMenuActions,
  cronTickerMenuActions,
  cronRunNowReason,
  CANCEL_ONLY_RUNNING,
  CANCEL_NOT_RUNNING_HERE,
  DELETE_WHILE_RUNNING,
} from '@/utilities/rowActions'

const byKey = (actions: ReturnType<typeof timeTickerMenuActions>, key: string) => actions.find((a) => a.key === key)!

describe('time ticker menu', () => {
  it('lets a row that is not running be deleted but not cancelled, and says why', () => {
    const actions = timeTickerMenuActions({ isInProgress: false, canBeForceDeleted: false })
    expect(byKey(actions, 'cancel').disabledReason).toBe(CANCEL_ONLY_RUNNING)
    expect(byKey(actions, 'delete').disabledReason).toBeUndefined()
    expect(byKey(actions, 'delete').label).toBe('Delete')
  })

  it('lets a running row be cancelled but not deleted, and says why', () => {
    const actions = timeTickerMenuActions({ isInProgress: true, canBeForceDeleted: false })
    expect(byKey(actions, 'cancel').disabledReason).toBeUndefined()
    expect(byKey(actions, 'delete').disabledReason).toBe(DELETE_WHILE_RUNNING)
  })

  it('offers Force delete once a cancel found nothing to cancel', () => {
    const actions = timeTickerMenuActions({ isInProgress: true, canBeForceDeleted: true })
    expect(byKey(actions, 'cancel').disabledReason).toBe(CANCEL_NOT_RUNNING_HERE)
    expect(byKey(actions, 'delete').label).toBe('Force delete')
    expect(byKey(actions, 'delete').disabledReason).toBeUndefined()
  })

  it('puts the destructive entry last', () => {
    const actions = timeTickerMenuActions({ isInProgress: false, canBeForceDeleted: false })
    expect(actions[actions.length - 1].danger).toBe(true)
    expect(actions.filter((a) => a.danger)).toHaveLength(1)
  })
})

describe('cron ticker menu', () => {
  it('names the toggle after what it will do', () => {
    expect(byKey(cronTickerMenuActions({ isEnabled: true, chartLoading: false }), 'toggle').label).toBe('Disable')
    expect(byKey(cronTickerMenuActions({ isEnabled: false, chartLoading: false }), 'toggle').label).toBe('Enable')
  })

  it('blocks the chart entry while the chart loads', () => {
    expect(byKey(cronTickerMenuActions({ isEnabled: true, chartLoading: true }), 'chart').disabledReason).toBeDefined()
    expect(byKey(cronTickerMenuActions({ isEnabled: true, chartLoading: false }), 'chart').disabledReason).toBeUndefined()
  })

  it('puts the destructive entry last', () => {
    const actions = cronTickerMenuActions({ isEnabled: true, chartLoading: false })
    expect(actions[actions.length - 1]).toMatchObject({ key: 'delete', danger: true })
  })

  it('explains why a disabled cron cannot run now', () => {
    expect(cronRunNowReason(false)).toBeDefined()
    expect(cronRunNowReason(true)).toBeUndefined()
  })
})
