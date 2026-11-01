import { describe, it, expect, vi, beforeEach } from 'vitest';

const mockRequest = vi.fn().mockResolvedValue({ data: { items: [], totalCount: 0 }, headers: {} });

vi.mock('axios', () => {
    const CancelToken = { source: () => ({ token: {}, cancel: vi.fn() }) };
    return {
        default: {
            create: () => ({
                request: mockRequest,
                interceptors: {
                    request: { use: vi.fn() },
                    response: { use: vi.fn() },
                },
            }),
            CancelToken: Object.assign(function (executor: (cancel: Function) => void) {
                executor(vi.fn());
            }, { source: CancelToken.source }),
        },
        AxiosError: class AxiosError extends Error {},
    };
});

vi.mock('vue', () => ({
    ref: (val: any) => ({ value: val }),
}));

vi.mock('@/composables/useAlert', () => ({
    useAlert: () => ({ showHttpError: vi.fn() }),
}));

vi.mock('@/stores/authStore', () => ({
    useAuthStore: () => ({ handle401Error: vi.fn() }),
}));
vi.mock('@/stores/alertStore', () => ({
    useAlertStore: () => ({ showHttpError: vi.fn() }),
}));
vi.mock('@/stores/functionNames', () => ({
    useFunctionNameStore: () => ({ getNamespaceOrNull: () => null }),
}));
vi.mock('@/stores/timeZoneStore', () => ({
    useTimeZoneStore: () => ({ effectiveTimeZone: 'UTC' }),
}));
vi.mock('@/http/services/tickerService', () => ({ tickerService: {} }));

vi.mock('@/utilities/pathResolver', () => ({
    getApiBaseUrl: () => '/api',
    getAuthMode: () => 'none',
}));

vi.mock('@/http/services/types/base/baseHttpResponse.types', () => ({
    getStatusValueSafe: (v: any) => v,
    Status: {},
}));

describe('cronTickerService enabled filter', () => {
    beforeEach(() => {
        mockRequest.mockClear();
        mockRequest.mockResolvedValue({ data: { items: [], totalCount: 3 }, headers: {} });
    });

    it('sends no isEnabled when the list is not limited', async () => {
        const { cronTickerService } = await import('@/http/services/cronTickerService');

        await cronTickerService.getCronTickersPaginated().requestAsync(1, 20);

        expect(mockRequest.mock.calls[0][0].params).toEqual({ pageNumber: 1, pageSize: 20 });
    });

    it('sends isEnabled=false to list only the disabled crons', async () => {
        const { cronTickerService } = await import('@/http/services/cronTickerService');

        await cronTickerService.getCronTickersPaginated().requestAsync(1, 20, false);

        expect(mockRequest.mock.calls[0][0].params).toEqual({ pageNumber: 1, pageSize: 20, isEnabled: false });
    });

    it('reads the number of disabled crons from the total of a one item page', async () => {
        const { cronTickerService } = await import('@/http/services/cronTickerService');

        const count = await cronTickerService.getDisabledCount().requestAsync();

        expect(count).toBe(3);
        expect(mockRequest.mock.calls[0][0].params).toEqual({ pageNumber: 1, pageSize: 1, isEnabled: false });
    });
});
