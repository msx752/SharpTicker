/**
 * How the Time Tickers and Cron Tickers pages open: the table and the chart data are requested together and never wait
 * for the WebSocket connection, the charts are drawn only after the table has painted, and the hub listeners are
 * attached once the connection has settled, whether it worked or not.
 */
import { describe, expect, it } from 'vitest'
import { runInitialLoad, type InitialLoadSteps } from '@/utilities/initialLoad'

const deferred = <T = void>() => {
  let resolve!: (v: T) => void
  let reject!: (e: unknown) => void
  const promise = new Promise<T>((res, rej) => {
    resolve = res
    reject = rej
  })
  return { promise, resolve, reject }
}

const flush = () => new Promise((r) => setTimeout(r, 0))

function setup(overrides: Partial<InitialLoadSteps> = {}) {
  const log: string[] = []
  const connect = deferred()
  const table = deferred()
  const chartA = deferred()
  const chartB = deferred()
  const paint = deferred()

  const steps: InitialLoadSteps = {
    connect: () => { log.push('connect'); return connect.promise },
    loadTable: () => { log.push('table'); return table.promise },
    loadCharts: [
      () => { log.push('chartA'); return chartA.promise },
      () => { log.push('chartB'); return chartB.promise },
    ],
    showCharts: () => { log.push('showCharts') },
    attachListeners: async () => { log.push('listeners') },
    afterPaint: () => { log.push('paint-wait'); return paint.promise },
    ...overrides,
  }
  const done = runInitialLoad(steps)
  return { log, connect, table, chartA, chartB, paint, done }
}

describe('runInitialLoad', () => {
  it('requests the table and the chart data together, without waiting for the connection', () => {
    const { log } = setup()

    expect(log).toEqual(['connect', 'table', 'chartA', 'chartB'])
  })

  it('does not draw the charts before the table has loaded', async () => {
    const { log, chartA, chartB } = setup()

    chartA.resolve()
    chartB.resolve()
    await flush()

    expect(log).not.toContain('showCharts')
  })

  it('draws the charts after the table has loaded and a frame has been painted', async () => {
    const { log, table, paint } = setup()

    table.resolve()
    await flush()
    expect(log).toContain('paint-wait')
    expect(log).not.toContain('showCharts')

    paint.resolve()
    await flush()
    expect(log.indexOf('showCharts')).toBeGreaterThan(log.indexOf('paint-wait'))
  })

  it('still draws the charts when the table fails to load', async () => {
    const { log, table, paint } = setup()

    table.reject(new Error('boom'))
    await flush()
    paint.resolve()
    await flush()

    expect(log).toContain('showCharts')
  })

  it('attaches the hub listeners only after the connection has settled', async () => {
    const { log, connect } = setup()
    await flush()
    expect(log).not.toContain('listeners')

    connect.resolve()
    await flush()
    expect(log).toContain('listeners')
  })

  it('attaches the hub listeners when the connection fails, as before', async () => {
    const { log, connect } = setup()

    connect.reject(new Error('no socket'))
    await flush()

    expect(log).toContain('listeners')
  })

  it('does not stop the other steps when one chart request fails', async () => {
    const { log, table, chartA, chartB, paint, connect, done } = setup()

    chartA.reject(new Error('boom'))
    chartB.resolve()
    table.resolve()
    paint.resolve()
    connect.resolve()
    await done

    expect(log).toContain('showCharts')
    expect(log).toContain('listeners')
  })

  it('does nothing further once the page has been left', async () => {
    let active = true
    const { log, table, chartA, chartB, paint, connect, done } = setup({ isActive: () => active })

    active = false
    chartA.resolve()
    chartB.resolve()
    table.resolve()
    paint.resolve()
    connect.resolve()
    await done

    expect(log).not.toContain('showCharts')
    expect(log).not.toContain('listeners')
  })

  it('resolves when everything has settled', async () => {
    const { table, chartA, chartB, paint, connect, done } = setup()

    table.resolve()
    chartA.resolve()
    chartB.resolve()
    paint.resolve()
    connect.resolve()

    await expect(done).resolves.toBeUndefined()
  })
})
