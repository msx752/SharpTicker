/**
 * The order in which the Time Tickers and Cron Tickers pages open.
 *
 * Before, the page did one thing after the other: WebSocket connection (up to 3 attempts), table, time series chart,
 * pie chart, hub listeners. The table does not need the connection, so it waited for nothing, and the charts were built
 * with an empty option before the table existed, so their setup competed with the table for the first frame.
 *
 * Now:
 *  - the connection, the table and the chart data are requested at the same time;
 *  - the charts are drawn after the table has loaded and one frame has been painted, so the table appears first;
 *  - the hub listeners are attached when the connection has settled, whether it worked or not, as before.
 *
 * A step that fails does not stop the others: each page handles its own errors, as it did.
 */
export interface InitialLoadSteps {
  /** Starts the WebSocket connection; may take long or fail, and nothing but the listeners waits for it. */
  connect: () => Promise<unknown>
  loadTable: () => Promise<unknown>
  /** The requests that feed the charts. They start with the table; drawing waits for it. */
  loadCharts: Array<() => Promise<unknown>>
  /** Lets the page draw the charts (they are not built before this is called). */
  showCharts: () => void
  attachListeners: () => Promise<unknown> | void
  /** Resolves after the next frame has been painted. Replaceable for tests. */
  afterPaint?: () => Promise<void>
  /** False once the page has been left; later steps are then skipped. */
  isActive?: () => boolean
}

const nextPaint = () =>
  new Promise<void>((resolve) => {
    if (typeof requestAnimationFrame !== 'function') {
      resolve()
      return
    }
    requestAnimationFrame(() => setTimeout(resolve, 0))
  })

/** Runs a step now and turns any failure into "done": the pages swallow these errors anyway. */
function settle(step: () => Promise<unknown> | void): Promise<void> {
  try {
    return Promise.resolve(step()).then(
      () => undefined,
      () => undefined,
    )
  } catch {
    return Promise.resolve()
  }
}

export async function runInitialLoad(steps: InitialLoadSteps): Promise<void> {
  const isActive = steps.isActive ?? (() => true)
  const afterPaint = steps.afterPaint ?? nextPaint

  const connection = settle(steps.connect)
  const table = settle(steps.loadTable)
  const charts = steps.loadCharts.map(settle)

  const listeners = connection.then(() => (isActive() ? settle(steps.attachListeners) : undefined))

  const drawn = table.then(async () => {
    if (!isActive()) return
    await afterPaint()
    if (isActive()) steps.showCharts()
  })

  await Promise.all([connection, table, ...charts, listeners, drawn])
}
