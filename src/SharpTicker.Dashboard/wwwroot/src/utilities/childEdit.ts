/**
 * What the time ticker dialog offers in place of a date and a time, for the row it was opened with.
 *
 * A chain child can have no time of its own: its parent's run starts it. Editing such a child must not force a time on
 * it, because a child with a time waits for that time as well. Any other stored ticker needs a time: the server
 * refuses an update without one.
 */
export interface EditedRow {
  id?: string
  isFromDuplicate: boolean
  parentId?: string | null
  executionTime?: string | null
  runCondition?: string | number | null
}

export type NoTimeChoice =
  /** A new ticker or a copy: without a time it runs at once. */
  | 'immediately'
  /** A stored child without a time: it can stay that way. */
  | 'startedByParent'
  /** A stored ticker that has a time: a time is required. */
  | 'none'

export const EXECUTE_IMMEDIATELY_LABEL = 'Execute immediately (with a one-second delay)'
export const STARTED_BY_PARENT_LABEL = "No time of its own (its parent's run starts it)"

const isStoredRow = (row: EditedRow) => !row.isFromDuplicate && row.id != undefined

/** A stored chain child: it has a parent and is being edited, not copied. */
export const isStoredChild = (row: EditedRow) => isStoredRow(row) && !!row.parentId

export function noTimeChoice(row: EditedRow): NoTimeChoice {
  if (!isStoredRow(row)) return 'immediately'
  return isStoredChild(row) && !row.executionTime ? 'startedByParent' : 'none'
}

export function noTimeLabel(choice: NoTimeChoice): string | undefined {
  if (choice === 'immediately') return EXECUTE_IMMEDIATELY_LABEL
  if (choice === 'startedByParent') return STARTED_BY_PARENT_LABEL
  return undefined
}

const RUN_CONDITIONS: [name: string, label: string][] = [
  ['OnSuccess', 'On Success'],
  ['OnFailure', 'On Failure'],
  ['OnCancelled', 'On Cancelled'],
  ['OnFailureOrCancelled', 'On Failure or Cancelled'],
  ['OnAnyCompletedStatus', 'On Any Completed Status'],
  ['InProgress', 'In Progress (Parallel)'],
]

/** The run condition of a child as text, from its number or its name; undefined when the row has none. */
export function runConditionLabel(value: string | number | null | undefined): string | undefined {
  if (value === null || value === undefined || value === '') return undefined
  const entry = typeof value === 'number' ? RUN_CONDITIONS[value] : RUN_CONDITIONS.find(([name]) => name === value)
  return entry ? entry[1] : String(value)
}
