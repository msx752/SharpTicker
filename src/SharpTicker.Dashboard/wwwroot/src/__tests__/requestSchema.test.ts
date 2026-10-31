import * as yup from 'yup'
import { describe, expect, it } from 'vitest'
import { requestDataRule } from '@/utilities/requestRule'

// The rule the ticker dialogs use, so these cases cover the real thing.
const schema = yup.object({ requestData: requestDataRule(yup) })

describe('requestDataRule', () => {
  it.each([undefined, null, ''])('accepts %j, so a function without a request can be cloned', async (value) => {
    await expect(schema.validate({ requestData: value })).resolves.toBeTruthy()
  })

  it('accepts valid JSON', async () => {
    await expect(schema.validate({ requestData: '{"OrderId":"A-1"}' })).resolves.toBeTruthy()
  })

  it('rejects invalid JSON and says where', async () => {
    await expect(schema.validate({ requestData: '{"a":' })).rejects.toThrow(/line|column|JSON/i)
  })
})
