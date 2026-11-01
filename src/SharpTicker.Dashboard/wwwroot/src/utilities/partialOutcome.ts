// A chain whose first job is Done can still have jobs below it that failed or were cancelled. The stored status of the
// first job stays Done (that is what happened to it), but the table should not read as if the whole chain went well.
// This is derived on screen only: nothing is stored and the API is unchanged.
//
// A skipped job does not count. A job that runs only on failure (or only on success) is skipped by design when its
// condition is not met, so a chain with only skipped jobs below it went as planned.

type Node = { status?: string | number; children?: Node[] | null }

// Mirrors the TickerStatus enum order.
const FAILED = 5
const CANCELLED = 6
const DONE = 3
const DUE_DONE = 4

const isStatus = (status: string | number | undefined, name: string, value: number) =>
  status === name || status === value

/** How many jobs below this one (at any depth) failed or were cancelled. */
export function countFailedBelow(node: Node): number {
  let count = 0
  for (const child of node.children ?? []) {
    if (isStatus(child.status, 'Failed', FAILED) || isStatus(child.status, 'Cancelled', CANCELLED)) count++
    count += countFailedBelow(child)
  }
  return count
}

/** True when this job is Done (or DueDone) but something below it failed or was cancelled. */
export function isPartiallyDone(node: Node): boolean {
  const done = isStatus(node.status, 'Done', DONE) || isStatus(node.status, 'DueDone', DUE_DONE)
  return done && countFailedBelow(node) > 0
}
