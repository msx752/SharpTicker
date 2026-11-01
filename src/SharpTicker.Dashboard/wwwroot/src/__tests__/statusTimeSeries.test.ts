/**
 * The status chart of the dashboard: how the per-day counts the server sends become series, legend, colours, axis and
 * the message shown when nothing happened.
 */
import { describe, expect, it } from 'vitest'
import { buildStatusChartOption, buildStatusTimeSeries, STATUS_COLORS, ZOOM_SLIDER_MIN_DAYS } from '@/utilities/statusTimeSeries'

const name = (status: number) => ['Idle', 'Queued', 'InProgress', 'Done', 'DueDone', 'Failed', 'Cancelled', 'Skipped'][status]

describe('buildStatusTimeSeries', () => {
  it.each([undefined, null])('returns an empty result for %s', (days) => {
    expect(buildStatusTimeSeries(days, name)).toEqual({ xAxisData: [], series: [], legendData: [], hasData: false })
  })

  it('keeps the days in the order the server sent them', () => {
    // Sorting these as text would give 01/11, 02/11, 27/10, 28/10.
    const days = ['27/10/2026', '28/10/2026', '01/11/2026', '02/11/2026'].map((date) => ({
      date,
      results: [{ item1: 3, item2: 1 }],
    }))

    expect(buildStatusTimeSeries(days, name).xAxisData).toEqual(['27/10/2026', '28/10/2026', '01/11/2026', '02/11/2026'])
  })

  it('has no data when every count is zero, and draws no series', () => {
    const days = [
      { date: '30/10/2026', results: [{ item1: 7, item2: 0 }] },
      { date: '31/10/2026', results: [{ item1: 3, item2: 0 }] },
    ]

    const result = buildStatusTimeSeries(days, name)

    expect(result.hasData).toBe(false)
    expect(result.series).toEqual([])
    expect(result.legendData).toEqual([])
    expect(result.xAxisData).toEqual(['30/10/2026', '31/10/2026'])
  })

  it('leaves out statuses that never occur and orders the rest by status id', () => {
    const days = [
      { date: 'd1', results: [{ item1: 5, item2: 2 }, { item1: 3, item2: 4 }, { item1: 7, item2: 0 }] },
      { date: 'd2', results: [{ item1: 3, item2: 1 }] },
    ]

    const result = buildStatusTimeSeries(days, name)

    expect(result.legendData).toEqual(['Done', 'Failed'])
    expect(result.series.map((s) => s.data)).toEqual([[4, 1], [2, 0]])
  })

  it('draws a bar series per status in the status colour', () => {
    const result = buildStatusTimeSeries([{ date: 'd1', results: [{ item1: 5, item2: 1 }] }], name)

    expect(result.series[0]).toMatchObject({
      type: 'bar',
      itemStyle: { color: STATUS_COLORS[5] },
    })
  })

  it('uses a fallback colour and the supplied name for an unknown status', () => {
    const result = buildStatusTimeSeries([{ date: 'd1', results: [{ item1: 99, item2: 1 }] }], (s) => `Status ${s}`)

    expect(result.series[0].name).toBe('Status 99')
    expect(result.series[0].itemStyle.color).toBe('#999999')
  })

  it('treats days without results as empty and keeps them on the axis', () => {
    const days = [
      { date: 'd1', results: null },
      { date: 'd2' },
      { date: 'd3', results: [{ item1: 3, item2: 2 }] },
    ]

    const result = buildStatusTimeSeries(days, name)

    expect(result.xAxisData).toEqual(['d1', 'd2', 'd3'])
    expect(result.series[0].data).toEqual([0, 0, 2])
  })

  it('adds up counts when the same date and status appear twice', () => {
    const days = [
      { date: 'd1', results: [{ item1: 3, item2: 2 }] },
      { date: 'd1', results: [{ item1: 3, item2: 5 }] },
    ]

    const result = buildStatusTimeSeries(days, name)

    expect(result.xAxisData).toEqual(['d1'])
    expect(result.series[0].data).toEqual([7])
  })
})

describe('buildStatusChartOption', () => {
  const days = (count: number) =>
    Array.from({ length: count }, (_, i) => ({ date: `d${i}`, results: [{ item1: 3, item2: 1 }] }))

  it('shows only the statuses that occurred in the legend', () => {
    const chart = buildStatusTimeSeries([{ date: 'd1', results: [{ item1: 5, item2: 1 }, { item1: 3, item2: 0 }] }], name)

    expect(buildStatusChartOption(chart, 'sub').legend.data).toEqual(['Failed'])
  })

  it('keeps the legend clear of the title', () => {
    const option = buildStatusChartOption(buildStatusTimeSeries(days(3), name), 'sub')

    expect(option.legend.top).toBeGreaterThan(option.title.top)
    expect(option.grid.top).toBeGreaterThan(option.legend.top)
  })

  it('shows a message instead of an empty plot when nothing happened', () => {
    const option = buildStatusChartOption(buildStatusTimeSeries([{ date: 'd1', results: [{ item1: 3, item2: 0 }] }], name), 'sub')

    expect(option.graphic).toHaveLength(1)
    expect(option.series).toEqual([])
  })

  it('has no message when there is data', () => {
    expect(buildStatusChartOption(buildStatusTimeSeries(days(3), name), 'sub').graphic).toEqual([])
  })

  it('only adds the zoom slider for long ranges', () => {
    expect(buildStatusChartOption(buildStatusTimeSeries(days(ZOOM_SLIDER_MIN_DAYS), name), 's').dataZoom).toEqual([])
    expect(buildStatusChartOption(buildStatusTimeSeries(days(ZOOM_SLIDER_MIN_DAYS + 1), name), 's').dataZoom).toHaveLength(2)
  })

  it('shows whole numbers on the count axis and never goes below zero', () => {
    const { yAxis } = buildStatusChartOption(buildStatusTimeSeries(days(2), name), 's')

    expect(yAxis.minInterval).toBe(1)
    expect(yAxis.min).toBe(0)
  })
})
