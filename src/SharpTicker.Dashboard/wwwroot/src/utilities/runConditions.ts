/**
 * The run conditions of a chain job, as the dashboard names them. The values are those of RunCondition on the
 * server; On Success is 0, so a value must never be tested for truthiness.
 */
export const RUN_CONDITIONS: { title: string; value: number }[] = [
  { title: 'On Success', value: 0 },
  { title: 'On Failure', value: 1 },
  { title: 'On Cancelled', value: 2 },
  { title: 'On Failure or Cancelled', value: 3 },
  { title: 'On Any Completed Status', value: 4 },
  { title: 'In Progress (Parallel)', value: 5 },
]

/**
 * The name of a run condition; "None" when none is chosen, the value itself when it is unknown. Form fields may hold
 * the value as a number or as its text.
 */
export function runConditionLabel(value: number | string | null | undefined): string {
  if (value === null || value === undefined || value === '') return 'None'
  return RUN_CONDITIONS.find((c) => c.value === Number(value))?.title ?? String(value)
}
