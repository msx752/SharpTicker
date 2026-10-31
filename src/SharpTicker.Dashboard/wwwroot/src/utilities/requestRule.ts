import type * as Yup from 'yup'
import { checkJson } from '@/utilities/jsonText'

/**
 * Validation rule for the Request Data field of the ticker dialogs.
 *
 * ensure() turns undefined and null into an empty string. A function without a request type has no
 * request to show, so cloning or editing it hands the form undefined. Empty text is a valid request.
 */
export const requestDataRule = (validator: typeof Yup) =>
  validator
    .string()
    .ensure()
    .test('valid-json', 'Invalid JSON', (value, context) => {
      const check = checkJson(value)
      return check.valid || context.createError({ message: check.message })
    })
