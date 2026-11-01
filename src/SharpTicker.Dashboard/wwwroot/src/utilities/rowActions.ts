/**
 * The entries of the "more actions" menu in the ticker tables.
 *
 * Each row shows at most two actions inline (the ones used most, and harmless). Everything else sits in a menu, with
 * the destructive entry last and set apart. An entry that cannot be used now stays visible and says why, instead of
 * disappearing or being greyed out without a reason.
 */
export interface RowAction {
  key: string
  label: string
  icon: string
  /** Set when the entry cannot be used now; shown as the reason under the label. */
  disabledReason?: string
  /** Destructive entries are drawn in the error colour, after a divider. */
  danger?: boolean
}

export const CANCEL_ONLY_RUNNING = 'Only a running job can be cancelled.'
export const CANCEL_NOT_RUNNING_HERE = 'No job for this ticker runs on this node. Use Force delete to remove it.'
export const DELETE_WHILE_RUNNING = 'A running job cannot be deleted. Cancel it first.'

export interface TimeTickerRowState {
  isInProgress: boolean
  /** The cancel request found nothing to cancel, so the row may be force deleted. */
  canBeForceDeleted: boolean
}

export function timeTickerMenuActions(state: TimeTickerRowState): RowAction[] {
  const cancelReason = !state.isInProgress
    ? CANCEL_ONLY_RUNNING
    : state.canBeForceDeleted
      ? CANCEL_NOT_RUNNING_HERE
      : undefined

  const deleteReason = state.isInProgress && !state.canBeForceDeleted ? DELETE_WHILE_RUNNING : undefined

  return [
    { key: 'cancel', label: 'Cancel job', icon: 'mdi-cancel', disabledReason: cancelReason },
    {
      key: 'delete',
      label: state.canBeForceDeleted ? 'Force delete' : 'Delete',
      icon: 'mdi-trash-can',
      disabledReason: deleteReason,
      danger: true,
    },
  ]
}

export interface CronTickerRowState {
  isEnabled: boolean
  chartLoading: boolean
}

export function cronTickerMenuActions(state: CronTickerRowState): RowAction[] {
  return [
    {
      key: 'toggle',
      label: state.isEnabled ? 'Disable' : 'Enable',
      icon: 'mdi-power',
    },
    {
      key: 'chart',
      label: 'Show occurrences chart',
      icon: 'mdi-chart-areaspline',
      disabledReason: state.chartLoading ? 'The chart is loading.' : undefined,
    },
    { key: 'occurrences', label: 'View occurrences', icon: 'mdi-folder-open' },
    { key: 'delete', label: 'Delete', icon: 'mdi-trash-can', danger: true },
  ]
}

/** Why a cron cannot run on demand, or undefined when it can. */
export function cronRunNowReason(isEnabled: boolean): string | undefined {
  return isEnabled ? undefined : 'Enable the cron ticker to run it now.'
}
