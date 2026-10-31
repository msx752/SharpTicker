import { describe, it, expect, vi, beforeEach } from 'vitest'

/**
 * The dashboard API answers a refused action with HTTP 200 and { success: false, message }. The regression covered
 * here: the HTTP layer returned that as a normal response, so a dialog closed and reloaded its list as if the
 * action had worked.
 */

const mockRequest = vi.fn()
vi.mock('axios', () => {
  const CancelToken = { source: () => ({ token: {}, cancel: vi.fn() }) }
  return {
    default: {
      create: () => ({
        request: mockRequest,
        interceptors: { request: { use: vi.fn() }, response: { use: vi.fn() } },
      }),
      CancelToken: Object.assign(function (executor: (cancel: Function) => void) {
        executor(vi.fn())
      }, { source: CancelToken.source }),
    },
    AxiosError: class AxiosError extends Error {},
  }
})

vi.mock('vue', () => ({ ref: (val: any) => ({ value: val }) }))

const showError = vi.fn()
const showHttpError = vi.fn()
vi.mock('@/composables/useAlert', () => ({ useAlert: () => ({ showError, showHttpError }) }))

vi.mock('@/stores/authStore', () => ({ useAuthStore: () => ({ handle401Error: vi.fn() }) }))
vi.mock('@/stores/alertStore', () => ({ useAlertStore: () => ({ showHttpError: vi.fn() }) }))
vi.mock('@/utilities/pathResolver', () => ({ getApiBaseUrl: () => '/api', getAuthMode: () => 'none' }))
vi.mock('@/http/services/types/base/baseHttpResponse.types', () => ({ getStatusValueSafe: (v: any) => v }))

describe('a refused action', () => {
  beforeEach(() => {
    mockRequest.mockReset()
    showError.mockClear()
    showHttpError.mockClear()
  })

  it('is thrown, so the caller does not go on as if it had worked', async () => {
    mockRequest.mockResolvedValue({
      data: { success: false, message: 'Failed to add cron ticker: The cron expression 0 0 22-2 * * * has a range that runs backwards' },
      headers: {},
    })
    const { cronTickerService } = await import('@/http/services/cronTickerService')
    const proceed = vi.fn()

    await expect(
      cronTickerService
        .addCronTicker()
        .requestAsync({ function: 'Job', expression: '0 0 22-2 * * *' } as any)
        .then(proceed),
    ).rejects.toThrow('backwards')

    expect(proceed).not.toHaveBeenCalled()
  })

  it('tells the user why it was refused', async () => {
    mockRequest.mockResolvedValue({ data: { success: false, message: 'Failed to update cron ticker: bad' }, headers: {} })
    const { cronTickerService } = await import('@/http/services/cronTickerService')

    await expect(
      cronTickerService.updateCronTicker().requestAsync('id-1', { function: 'Job', expression: 'x' } as any),
    ).rejects.toThrow()

    expect(showError).toHaveBeenCalledTimes(1)
    expect(showError).toHaveBeenCalledWith('Failed to update cron ticker: bad')
  })

  it('also covers toggling and deleting', async () => {
    const { cronTickerService } = await import('@/http/services/cronTickerService')

    mockRequest.mockResolvedValue({ data: { success: false, message: 'Failed to toggle cron ticker' }, headers: {} })
    await expect(cronTickerService.toggleCronTicker().requestAsync('id-1', false)).rejects.toThrow('toggle')

    mockRequest.mockResolvedValue({ data: { success: false, message: 'Failed to delete cron ticker' }, headers: {} })
    await expect(cronTickerService.deleteCronTicker().requestAsync('id-1')).rejects.toThrow('delete')

    expect(showError).toHaveBeenCalledTimes(2)
  })

  it('does not alert when the caller asked for silence', async () => {
    mockRequest.mockResolvedValue({ data: { success: false, message: 'refused' }, headers: {} })
    const { useBaseHttpService } = await import('@/http/base/baseHttpService')
    const http = useBaseHttpService<object, object>('single')

    await expect(http.sendAsync('POST', 'x', { suppressAlert: true })).rejects.toThrow('refused')

    expect(showError).not.toHaveBeenCalled()
  })

  it('leaves a successful action alone', async () => {
    mockRequest.mockResolvedValue({ data: { success: true, message: 'Cron ticker added successfully', tickerId: 'abc' }, headers: {} })
    const { cronTickerService } = await import('@/http/services/cronTickerService')

    const result = await cronTickerService.addCronTicker().requestAsync({ function: 'Job', expression: '0 0 * * * *' } as any)

    expect(result.success).toBe(true)
    expect(showError).not.toHaveBeenCalled()
  })
})
