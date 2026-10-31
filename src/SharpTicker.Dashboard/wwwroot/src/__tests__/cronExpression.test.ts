/**
 * The cron check of the dashboard form: which six-field expressions it lets through, which it refuses, and the reason
 * it gives. The cases are the ones the server's own checks are tested with, so the form and the server agree.
 */
import { describe, expect, it } from 'vitest'
import { findCronProblem, isValidCronExpression } from '@/utilities/cronExpression'

describe('a valid expression', () => {
  it.each([
    '0 0 0 * * *',
    '* * * * * *',
    '*/15 * * * * *',
    '0 */5 * * * *',
    '0 0 9-17 * * 1-5',
    '0 0 0 1,15 * *',
    '0 30 2 * * 0',
    '59 59 23 31 12 6',
    '0 0/5 * * * *',
    '0 0 9 * * MON-FRI',
    '0 0 9 * * mon,wed,fri',
    '0 0 0 1 Jan,Jul *',
    '0 0 0 * JAN-MAR *',
    '0 0 9 * * Monday',
    '0 0 0 * Sept *',
    '0 0 9 * * Mo',
    '*/60 * * * * *',
    '0 0 0 * * 0/24',
    '  0  0  0  *  *  *  ',
  ])('is accepted: %s', (expression) => {
    expect(findCronProblem(expression)).toBeUndefined()
    expect(isValidCronExpression(expression)).toBe(true)
  })
})

describe('an expression that cannot be used', () => {
  it.each([
    '',
    '   ',
    'not a cron',
    '* * * *',
    '30 2 * * *',
    '* * * * * * *',
    '0 0 0 * * * junk',
    '60 * * * * *',
    '* 60 * * * *',
    '* * 24 * * *',
    '* * * 0 * *',
    '* * * 32 * *',
    '* * * * 0 *',
    '* * * * 13 *',
    '* * * * * 7',
    '5-1 * * * * *',
    '*/0 * * * * *',
    '1, * * * * *',
    ',1 * * * * *',
    '1,,2 * * * * *',
    '*-5 * * * * *',
    '1-3-5 * * * * *',
    '*/2/3 * * * * *',
    '1-* * * * * *',
    '- * * * * *',
    '1/ * * * * *',
    'a * * * * *',
    '0 0 0 ? * *',
    '0 0 0 L * *',
    '0 15 10 * * 1#2',
    '0 0 0 * MON *',
    '0 0 0 * * JAN',
    '0 0 0 * * MONDAYS',
    '0 0 0 * * MONXYZ',
    'MON * * * * *',
    '99999999999999999999 * * * * *',
  ])('is refused: %j', (expression) => {
    expect(findCronProblem(expression)).toBeTypeOf('string')
    expect(isValidCronExpression(expression)).toBe(false)
  })

  it('is refused when it is missing', () => {
    expect(findCronProblem(undefined)).toBe('Expression is required.')
    expect(findCronProblem(null)).toBe('Expression is required.')
  })
})

describe('a backwards range', () => {
  it.each(['22-2 0 0 * * *', '0 0 22-2 * * *', '0 0 0 25-5 * *', '0 0 0 * NOV-FEB *', '0 0 0 * * FRI-MON', '0 0 0 * * Friday-Monday'])(
    'is refused and the reason names the fix: %s',
    (expression) => {
      expect(findCronProblem(expression)).toMatch(/runs backwards.*22-23,0-2/)
    },
  )

  it('is not refused when written as two ranges', () => {
    expect(findCronProblem('0 0 22-23,0-2 * * *')).toBeUndefined()
  })
})

describe('the reason', () => {
  it.each([
    ['* * * *', /six fields/],
    ['0 0 0 * * 7', /Day of week: 7 is out of range \(0-6\)/],
    ['0 0 24 * * *', /Hour: 24 is out of range \(0-23\)/],
    ['0 0 0 0 * *', /Day of month: 0 is out of range \(1-31\)/],
    ['0 0 0 * 13 *', /Month: 13 is out of range \(1-12\)/],
    ['0 0 0 * * JAN', /Day of week: "JAN" is not a valid value/],
    ['0 0 0 * MON *', /Month: "MON" is not a valid value/],
    ['*/0 * * * * *', /Second: the step in "\*\/0" must be at least 1/],
    ['*/x * * * * *', /Second: the step in "\*\/x" must be a whole number/],
    ['1, * * * * *', /Second: a list cannot have an empty item/],
    ['*/2/3 * * * * *', /more than one step/],
  ])('says what is wrong with %j', (expression, reason) => {
    expect(findCronProblem(expression)).toMatch(reason)
  })

  it('names the first field that is wrong', () => {
    expect(findCronProblem('99 99 99 * * *')).toMatch(/^Second:/)
  })
})
