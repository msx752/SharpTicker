/**
 * The chain builder's review names the run condition chosen for each child. On Success is 0, which a truthiness test
 * turned into "None".
 */
import { describe, expect, it } from 'vitest'
import { RUN_CONDITIONS, runConditionLabel } from '@/utilities/runConditions'

describe('runConditionLabel', () => {
  it('names On Success, whose value is 0', () => {
    expect(runConditionLabel(0)).toBe('On Success')
  })

  it.each(RUN_CONDITIONS)('names $title ($value)', ({ title, value }) => {
    expect(runConditionLabel(value)).toBe(title)
  })

  it('reads a value held as text', () => {
    expect(runConditionLabel('0')).toBe('On Success')
    expect(runConditionLabel('3')).toBe('On Failure or Cancelled')
  })

  it('says None when no condition is chosen', () => {
    expect(runConditionLabel('')).toBe('None')
    expect(runConditionLabel(null)).toBe('None')
    expect(runConditionLabel(undefined)).toBe('None')
  })

  it('shows an unknown value as it is', () => {
    expect(runConditionLabel(42)).toBe('42')
  })
})
