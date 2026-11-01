import { afterEach, describe, expect, it } from 'vitest'
import { init, use } from 'echarts/core'
import { SVGRenderer } from 'echarts/renderers'
import '@/utilities/echartsSetup'
import {
  buildStatusChartOption,
  buildStatusTimeSeries,
  STATUS_COLORS,
  type DayResults,
} from '@/utilities/statusTimeSeries'

// The dashboard draws on a canvas. Here the same options are drawn as SVG text so the test can read them.
// That runs everything ECharts does with them, including the lookup of every registered chart type and
// component, which the option builder tests never reach.
use([SVGRenderer])

const names = ['Idle', 'Queued', 'InProgress', 'Done', 'DueDone', 'Failed', 'Cancelled', 'Skipped']
const statusName = (status: number) => names[status]

const charts: { dispose: () => void }[] = []

function draw(days: DayResults[]) {
  const option = buildStatusChartOption(buildStatusTimeSeries(days, statusName), 'Job statuses for all Cron Tickers')
  const chart = init(null, undefined, { renderer: 'svg', ssr: true, width: 900, height: 420 })
  charts.push(chart)
  chart.setOption(option)
  return chart
}

const render = (days: DayResults[]): string => draw(days).renderToSVGString()

afterEach(() => {
  charts.splice(0).forEach((chart) => chart.dispose())
})

const day = (date: string, ...counts: [number, number][]): DayResults => ({
  date,
  results: counts.map(([item1, item2]) => ({ item1, item2 })),
})

describe('status chart rendering', () => {
  it('draws a line in the status colour and names the status in the legend', () => {
    const svg = render([day('27/10/2026', [3, 1]), day('28/10/2026', [3, 4]), day('29/10/2026', [3, 2])])

    expect(svg).toContain('<path')
    expect(svg.toLowerCase()).toContain(STATUS_COLORS[3].toLowerCase())
    expect(svg).toContain('>Done<')
  })

  it('shows the title and the subtitle', () => {
    const svg = render([day('27/10/2026', [3, 1])])

    expect(svg).toContain('Time Series Analysis')
    expect(svg).toContain('Job statuses for all Cron Tickers')
  })

  it('draws a legend entry and a line for each status that occurred, and nothing for the others', () => {
    const svg = render([day('d1', [3, 2], [5, 1], [7, 0])])

    expect(svg).toContain('>Done<')
    expect(svg).toContain('>Failed<')
    expect(svg).not.toContain('>Skipped<')
    expect(svg.toLowerCase()).toContain(STATUS_COLORS[5].toLowerCase())
  })

  it('writes the days on the axis in the order the server sent them, not sorted as text', () => {
    const svg = render(['27/10/2026', '28/10/2026', '01/11/2026', '02/11/2026'].map((date) => day(date, [3, 1])))

    const positions = ['27/10/2026', '28/10/2026', '01/11/2026', '02/11/2026'].map((date) => svg.indexOf(`>${date}<`))
    expect(positions.every((position) => position >= 0)).toBe(true)
    expect([...positions].sort((a, b) => a - b)).toEqual(positions)
  })

  it('says so when nothing ran, instead of drawing an empty plot', () => {
    const svg = render([day('27/10/2026', [3, 0]), day('28/10/2026', [7, 0])])

    expect(svg).toContain('No executions in this range')
    expect(svg).not.toContain('>Done<')
  })

  it('adds the zoom slider only for a long range', () => {
    const days = (count: number) => Array.from({ length: count }, (_, i) => day(`d${i}`, [3, 1]))

    // getOption only returns components that are registered, so this also fails if DataZoomComponent is not.
    expect(draw(days(20)).getOption().dataZoom).toHaveLength(2)
    expect(draw(days(5)).getOption().dataZoom).toEqual([])
  })

  // ECharts drops the options of a component that is not registered. The tooltip is the exception: its option is
  // kept either way, so a missing TooltipComponent is not caught here and has to be checked by hand.
  it('keeps every component the option asks for, which only happens when it is registered', () => {
    const days = Array.from({ length: 20 }, (_, i) => day(`d${i}`, [3, 1]))
    const asked = buildStatusChartOption(buildStatusTimeSeries(days, statusName), 'subtitle')
    const kept = draw(days).getOption() as Record<string, unknown>
    const notComponents = ['backgroundColor', 'animation', 'animationDuration', 'animationEasing', 'series', 'graphic']

    for (const key of Object.keys(asked).filter((name) => !notComponents.includes(name))) {
      expect(kept[key], `${key} is not registered in echartsSetup`).toBeDefined()
    }
  })

  it('does not say nothing ran when something did', () => {
    expect(render([day('27/10/2026', [3, 1])])).not.toContain('No executions in this range')
  })
})
