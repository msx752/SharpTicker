export const STATUS_COLORS: Record<number, string> = {
  0: '#A9A9A9', // Idle
  1: '#00CED1', // Queued
  2: '#6495ED', // InProgress
  3: '#32CD32', // Done
  4: '#008000', // DueDone
  5: '#FF0000', // Failed
  6: '#FFD700', // Cancelled
  7: '#BA68C8', // Skipped
}

const FALLBACK_COLOR = '#999999'

export interface StatusCount {
  item1: number
  item2: number
}

export interface DayResults {
  date: string
  results?: StatusCount[] | null
}

export interface StatusBarSeries {
  name: string
  type: 'bar'
  stack: 'total'
  barMaxWidth: number
  itemStyle: { color: string }
  emphasis: { focus: 'series' }
  data: number[]
}

export interface StatusTimeSeries {
  xAxisData: string[]
  series: StatusBarSeries[]
  legendData: string[]
  hasData: boolean
}

/** More days than this and the chart gets a zoom slider; below it the slider only takes room. */
export const ZOOM_SLIDER_MIN_DAYS = 14

/**
 * ECharts option for the stacked status bars. Shared by the Time and Cron ticker pages so both
 * charts look and behave the same, including the "nothing happened" state.
 */
export function buildStatusChartOption(chart: StatusTimeSeries, subtitle: string) {
  const days = chart.xAxisData.length
  const showZoom = days > ZOOM_SLIDER_MIN_DAYS

  return {
    backgroundColor: 'transparent',
    title: {
      text: 'Time Series Analysis',
      subtext: subtitle,
      left: 'center',
      top: 8,
      textStyle: { color: '#e0e0e0', fontSize: 14, fontWeight: 600 },
      subtextStyle: { color: '#9e9e9e', fontSize: 11 },
    },
    tooltip: {
      trigger: 'axis',
      axisPointer: { type: 'shadow' },
      backgroundColor: 'rgba(42, 42, 42, 0.95)',
      borderColor: '#666',
      borderWidth: 1,
      textStyle: { color: '#e0e0e0' },
    },
    legend: {
      type: 'scroll',
      data: chart.legendData,
      top: 56,
      textStyle: { color: '#e0e0e0', fontSize: 11 },
      itemGap: 16,
      itemWidth: 12,
      itemHeight: 12,
    },
    grid: {
      left: '4%',
      right: '4%',
      top: 96,
      bottom: showZoom ? 56 : 16,
      containLabel: true,
    },
    xAxis: {
      type: 'category',
      data: chart.xAxisData,
      axisLabel: { color: '#bdbdbd', fontSize: 10, rotate: days > 8 ? 45 : 0 },
      axisLine: { lineStyle: { color: '#424242' } },
    },
    yAxis: {
      type: 'value',
      minInterval: 1,
      axisLabel: { color: '#bdbdbd', fontSize: 10 },
      axisLine: { lineStyle: { color: '#424242' } },
      splitLine: { lineStyle: { color: 'rgba(255, 255, 255, 0.05)', type: 'dashed' } },
    },
    dataZoom: showZoom
      ? [
          { type: 'inside', start: 0, end: 100 },
          {
            show: true,
            type: 'slider',
            bottom: 8,
            start: 0,
            end: 100,
            backgroundColor: 'rgba(47, 47, 47, 0.5)',
            borderColor: '#666',
            fillerColor: 'rgba(100, 181, 246, 0.2)',
            handleStyle: { color: '#64b5f6', borderColor: '#64b5f6' },
          },
        ]
      : [],
    series: chart.series,
    graphic: chart.hasData
      ? []
      : [
          {
            type: 'text',
            left: 'center',
            top: 'middle',
            style: { text: 'No executions in this range', fill: '#9e9e9e', fontSize: 14 },
          },
        ],
    animation: true,
    animationDuration: 600,
    animationEasing: 'cubicOut' as const,
  }
}

/**
 * Turns the per-day status counts returned by the API into stacked bar series.
 *
 * Days keep the order the server sent them in (the date strings are locale formatted, so sorting
 * them as text would put 01/11 before 27/10). Statuses that never occur in the range are left out,
 * so the chart and its legend only show what actually happened.
 */
export function buildStatusTimeSeries(
  days: DayResults[] | null | undefined,
  statusName: (status: number) => string,
): StatusTimeSeries {
  if (!days || !Array.isArray(days)) {
    return { xAxisData: [], series: [], legendData: [], hasData: false }
  }

  const xAxisData: string[] = []
  const dateIndex = new Map<string, number>()
  for (const day of days) {
    if (day && !dateIndex.has(day.date)) {
      dateIndex.set(day.date, xAxisData.length)
      xAxisData.push(day.date)
    }
  }

  const countsByStatus = new Map<number, number[]>()
  for (const day of days) {
    if (!day || !Array.isArray(day.results)) continue
    const index = dateIndex.get(day.date)!
    for (const { item1, item2 } of day.results) {
      if (!item2) continue
      let counts = countsByStatus.get(item1)
      if (!counts) {
        counts = new Array<number>(xAxisData.length).fill(0)
        countsByStatus.set(item1, counts)
      }
      counts[index] += item2
    }
  }

  const series = [...countsByStatus.entries()]
    .sort(([a], [b]) => a - b)
    .map(([status, data]): StatusBarSeries => ({
      name: statusName(status),
      type: 'bar',
      stack: 'total',
      barMaxWidth: 40,
      itemStyle: { color: STATUS_COLORS[status] ?? FALLBACK_COLOR },
      emphasis: { focus: 'series' },
      data,
    }))

  return {
    xAxisData,
    series,
    legendData: series.map((s) => s.name),
    hasData: series.length > 0,
  }
}
