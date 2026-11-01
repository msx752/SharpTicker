/**
 * What the Registered Functions table says about a priority.
 *
 * Only LongRunning has an effect today: the job runs outside the worker pool. High, Normal and Low are accepted but the
 * scheduler does not order jobs by them, so the table says so instead of implying an order that is not applied.
 */
export function priorityNote(priority: string): string {
  return priority === 'LongRunning'
    ? 'Runs outside the worker pool.'
    : 'Not applied yet: this value does not change when the job runs.'
}

export const isPriorityApplied = (priority: string): boolean => priority === 'LongRunning'
