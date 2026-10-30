/**
 * The dashboard API answers an action it refused with HTTP 200 and `{ success: false, message }`, for example a cron
 * expression the server cannot accept. Without this check the caller sees an ordinary response and goes on as if the
 * action had worked, closing its dialog and reloading the list.
 */
export class ActionFailedError extends Error {
  constructor(message: string) {
    super(message)
    this.name = 'ActionFailedError'
  }
}

/** The message of a refused action, or undefined when the response is not a refusal. */
export function getActionFailure(data: unknown): string | undefined {
  if (data === null || typeof data !== 'object') return undefined

  const body = data as { success?: unknown; message?: unknown }
  if (body.success !== false) return undefined

  return typeof body.message === 'string' && body.message.length > 0
    ? body.message
    : 'The request was refused.'
}
