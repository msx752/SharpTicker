/**
 * What the Registered Functions table says about a priority. It decides which waiting job a free worker takes when more
 * jobs are ready than there are workers; it does not change when a job becomes due and does not stop a running job.
 */
export function priorityNote(priority: string): string {
  switch (priority) {
    case 'LongRunning':
      return 'Runs outside the worker pool.'
    case 'High':
      return 'When jobs are waiting for a worker, High jobs start first.'
    case 'Low':
      return 'When jobs are waiting for a worker, Low jobs start after High and Normal jobs.'
    default:
      return 'When jobs are waiting for a worker, Normal jobs start after High and before Low jobs.'
  }
}
