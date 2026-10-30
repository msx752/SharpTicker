import { use } from 'echarts/core'
import { CanvasRenderer } from 'echarts/renderers'
import { LineChart, PieChart } from 'echarts/charts'
import {
  DataZoomComponent,
  GraphicComponent,
  GridComponent,
  LegendComponent,
  TitleComponent,
  ToolboxComponent,
  TooltipComponent,
} from 'echarts/components'

// Everything the dashboard charts draw with, registered once for all pages. ECharts only draws what is
// registered: a series or component that is missing renders nothing, without an error in a production
// build. Add the chart type or component here when an option starts to use it; the chart tests render
// the real options, so a missing one shows up as a failing test.
use([
  CanvasRenderer,
  LineChart,
  PieChart,
  DataZoomComponent,
  GraphicComponent,
  GridComponent,
  LegendComponent,
  TitleComponent,
  ToolboxComponent,
  TooltipComponent,
])
