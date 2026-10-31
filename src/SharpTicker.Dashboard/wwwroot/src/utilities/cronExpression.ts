/**
 * The check the dashboard runs on a cron expression before it sends it, written to give the same answer as the server
 * for the expressions the dashboard sends, which always have six fields (seconds first).
 *
 * A field takes `*`, a value, a list (`1,15`), a range (`9-17`) and a step (`*\/15`, `0-30/5`). Month and day of week
 * also take names in any case, and any start of the full name counts (`MON`, `Monday`, `Sept`). Two things are refused
 * on purpose: a range that runs backwards (`22-2`), which the scheduler would read as `2-22` instead of wrapping
 * around, and a step of zero, which means nothing.
 *
 * cronstrue is still used to describe a valid expression in words, but not to decide whether it is valid: it accepts
 * `7`, `?` and `L` in places where the server does not.
 */

interface CronField {
  name: string
  min: number
  max: number
  names?: string[]
  /** The number of the first name, so JANUARY is 1 and SUNDAY is 0. */
  nameBase?: number
}

const MONTHS = [
  'JANUARY', 'FEBRUARY', 'MARCH', 'APRIL', 'MAY', 'JUNE',
  'JULY', 'AUGUST', 'SEPTEMBER', 'OCTOBER', 'NOVEMBER', 'DECEMBER',
]
const DAYS = ['SUNDAY', 'MONDAY', 'TUESDAY', 'WEDNESDAY', 'THURSDAY', 'FRIDAY', 'SATURDAY']

const FIELDS: CronField[] = [
  { name: 'Second', min: 0, max: 59 },
  { name: 'Minute', min: 0, max: 59 },
  { name: 'Hour', min: 0, max: 23 },
  { name: 'Day of month', min: 1, max: 31 },
  { name: 'Month', min: 1, max: 12, names: MONTHS, nameBase: 1 },
  { name: 'Day of week', min: 0, max: 6, names: DAYS, nameBase: 0 },
]

const NUMBER = /^\d+$/

function valueOf(text: string, field: CronField): number | undefined {
  if (NUMBER.test(text)) return Number(text)

  if (field.names && /^[A-Za-z]+$/.test(text)) {
    const upper = text.toUpperCase()
    const index = field.names.findIndex((name) => name.startsWith(upper))
    if (index >= 0) return index + (field.nameBase ?? 0)
  }

  return undefined
}

function describeRange(field: CronField): string {
  return field.names ? `${field.min}-${field.max}, or a name` : `${field.min}-${field.max}`
}

function problemInField(text: string, field: CronField): string | undefined {
  for (const item of text.split(',')) {
    if (item === '') return `${field.name}: a list cannot have an empty item.`

    const slash = item.split('/')
    if (slash.length > 2) return `${field.name}: "${item}" has more than one step.`
    const [rangeText, stepText] = slash

    if (stepText !== undefined) {
      if (!NUMBER.test(stepText)) return `${field.name}: the step in "${item}" must be a whole number.`
      if (Number(stepText) < 1) return `${field.name}: the step in "${item}" must be at least 1.`
    }

    if (rangeText === '*') continue

    const parts = rangeText.split('-')
    if (parts.length > 2) return `${field.name}: "${rangeText}" is not a value or a range.`

    const values: number[] = []
    for (const part of parts) {
      const value = valueOf(part, field)
      if (value === undefined) {
        return `${field.name}: "${part}" is not a valid value (${describeRange(field)}).`
      }
      if (value < field.min || value > field.max) {
        return `${field.name}: ${part} is out of range (${field.min}-${field.max}).`
      }
      values.push(value)
    }

    if (values.length === 2 && values[0] > values[1]) {
      return `${field.name}: "${rangeText}" runs backwards. Use two ranges instead, for example 22-23,0-2.`
    }
  }

  return undefined
}

/** The reason an expression cannot be used, or undefined when it can. */
export function findCronProblem(expression: string | null | undefined): string | undefined {
  const text = (expression ?? '').trim()
  if (text === '') return 'Expression is required.'

  const fields = text.split(/\s+/)
  if (fields.length !== FIELDS.length) {
    return 'A cron expression has six fields: second, minute, hour, day of month, month and day of week.'
  }

  for (let i = 0; i < FIELDS.length; i++) {
    const problem = problemInField(fields[i], FIELDS[i])
    if (problem) return problem
  }

  return undefined
}

export const isValidCronExpression = (expression: string | null | undefined): boolean =>
  findCronProblem(expression) === undefined
