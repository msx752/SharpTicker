import type { AddTimeTickerRequest, GetTimeTickerResponse } from '@/http/services/types/timeTickerService.types'
import { parseDuration } from '@/utilities/dateTimeParser'

type RunAgainSource = Pick<GetTimeTickerResponse, 'function' | 'retries' | 'retryIntervals' | 'description'>

/**
 * The request that adds a new ticker doing what an existing row did: the same function, request, retries,
 * retry intervals and description.
 *
 * It has no execution time, which the server reads as "run right away". The stored request is passed in as
 * it came from the server and is not changed, so numbers such as 10.50 stay as they were written.
 */
export function buildRunAgainRequest(
  item: RunAgainSource,
  storedRequest: string | null | undefined,
): AddTimeTickerRequest {
  const retries = parseInt(`${item.retries}`)

  return {
    function: item.function,
    request: storedRequest ?? '',
    retries: Number.isNaN(retries) ? 0 : retries,
    description: item.description ?? '',
    // The list keeps the intervals as text such as "1m 30s"; the server wants seconds.
    retryIntervals: Array.isArray(item.retryIntervals) ? item.retryIntervals.map(parseDuration) : undefined,
  }
}
