using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SharpTicker.Utilities.DashboardDtos;
using SharpTicker.Utilities.Entities;
using SharpTicker.Utilities.Enums;
using SharpTicker.Utilities.Models;

namespace SharpTicker.Utilities.Interfaces
{
    /// <summary>
    /// Public dashboard query service producing flat DTOs for custom dashboards
    /// and reporting endpoints.
    /// </summary>
    public interface ITickerDashboardDataService<TTimeTicker, TCronTicker>
        where TTimeTicker : TimeTickerEntity<TTimeTicker>, new()
        where TCronTicker : CronTickerEntity, new()
    {
        // Time tickers
        Task<PaginationResult<TimeTickerFlatDto>> GetTimeTickersFlatAsync(
            TimeTickerQueryFilter filter, CancellationToken cancellationToken = default);

        Task<TimeTickerFlatDto> GetTimeTickerByIdAsync(
            Guid id, CancellationToken cancellationToken = default);

        Task<IList<TimeTickerFlatDto>> GetTimeTickerChildrenAsync(
            Guid parentId, CancellationToken cancellationToken = default);

        // Cron tickers
        Task<PaginationResult<CronTickerFlatDto>> GetCronTickersFlatAsync(
            CronTickerQueryFilter filter, CancellationToken cancellationToken = default);

        Task<CronTickerFlatDto> GetCronTickerByIdAsync(
            Guid id, CancellationToken cancellationToken = default);

        // Cron occurrences
        Task<PaginationResult<CronOccurrenceFlatDto>> GetCronOccurrencesFlatAsync(
            Guid cronTickerId, CronOccurrenceQueryFilter filter, CancellationToken cancellationToken = default);

        Task<CronOccurrenceFlatDto> GetCronOccurrenceByIdAsync(
            Guid id, CancellationToken cancellationToken = default);

        // Executions (unified)
        Task<PaginationResult<ExecutionFlatDto>> GetExecutionsFlatAsync(
            ExecutionQueryFilter filter, CancellationToken cancellationToken = default);

        // Request payloads
        Task<byte[]> GetTimeTickerRequestAsync(
            Guid id, CancellationToken cancellationToken = default);

        Task<byte[]> GetCronOccurrenceRequestAsync(
            Guid id, CancellationToken cancellationToken = default);

        // Stats
        Task<IList<(TickerStatus Status, int Count)>> GetOverallStatusesAsync(
            CancellationToken cancellationToken = default);

        // Overview
        Task<IList<TimeTickerFlatDto>> GetUpcomingTickersAsync(
            int count, CancellationToken cancellationToken = default);

        Task<IList<ExecutionFlatDto>> GetRecentActivityAsync(
            int count, CancellationToken cancellationToken = default);

        // Functions
        Task<IList<FunctionInfoDto>> GetAllFunctionsAsync(CancellationToken cancellationToken = default);

        // Host
        Task<HostStatusDto> GetHostStatusAsync(CancellationToken cancellationToken = default);

        Task<NextTickerDto> GetNextTickerAsync(CancellationToken cancellationToken = default);

        // Graphs
        Task<IList<GraphBucketDto>> GetTimeTickersGraphAsync(
            int pastDays, int futureDays, CancellationToken cancellationToken = default);

        Task<IList<GraphBucketDto>> GetCronTickersGraphAsync(
            int pastDays, int futureDays, CancellationToken cancellationToken = default);

        Task<IList<GraphBucketDto>> GetCronTickerGraphByIdAsync(
            Guid cronTickerId, int pastDays, int futureDays, CancellationToken cancellationToken = default);

        Task<IList<GraphBucketDto>> GetCronOccurrencesGraphAsync(
            Guid cronTickerId, CancellationToken cancellationToken = default);

        // ===== Operations =====

        Task<bool> ToggleCronTickerAsync(Guid cronTickerId, bool isEnabled, CancellationToken cancellationToken = default);

        Task RunCronTickerOnDemandAsync(Guid cronTickerId, CancellationToken cancellationToken = default);

        bool CancelTicker(Guid tickerId);

        Task StartHostAsync(CancellationToken cancellationToken = default);

        Task StopHostAsync(CancellationToken cancellationToken = default);

        void RestartHost();
    }
}
