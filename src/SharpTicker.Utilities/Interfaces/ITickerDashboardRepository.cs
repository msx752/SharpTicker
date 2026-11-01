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
    internal interface ITickerDashboardRepository<TTimeTicker, TCronTicker> 
        where TTimeTicker : TimeTickerEntity<TTimeTicker>, new()
        where TCronTicker : CronTickerEntity, new()
    {
        Task<TTimeTicker[]> GetTimeTickersAsync(CancellationToken cancellationToken = default, TickerVisibility<TTimeTicker, TCronTicker> visibility = null);
        Task<PaginationResult<TTimeTicker>> GetTimeTickersPaginatedAsync(int pageNumber, int pageSize, TickerStatus? status = null, string search = null, CancellationToken cancellationToken = default, TickerVisibility<TTimeTicker, TCronTicker> visibility = null);
        Task<IList<Tuple<TickerStatus, int>>> GetTimeTickerFullDataAsync(CancellationToken cancellationToken, TickerVisibility<TTimeTicker, TCronTicker> visibility = null);
        Task<IList<TickerGraphData>> GetTimeTickersGraphSpecificDataAsync(int pastDays, int futureDays,CancellationToken cancellationToken, TickerVisibility<TTimeTicker, TCronTicker> visibility = null);
        Task<IList<TickerGraphData>> GetCronTickersGraphSpecificDataAsync(int pastDays, int futureDays,CancellationToken cancellationToken, TickerVisibility<TTimeTicker, TCronTicker> visibility = null);
        Task<IList<TickerGraphData>> GetCronTickersGraphSpecificDataByIdAsync(Guid id, int pastDays, int futureDays,CancellationToken cancellationToken);
        Task<IList<Tuple<TickerStatus, int>>> GetCronTickerFullDataAsync(CancellationToken cancellationToken, TickerVisibility<TTimeTicker, TCronTicker> visibility = null);
        Task<CronTickerEntity[]> GetCronTickersAsync(CancellationToken cancellationToken = default, TickerVisibility<TTimeTicker, TCronTicker> visibility = null);
        Task<PaginationResult<CronTickerEntity>> GetCronTickersPaginatedAsync(int pageNumber, int pageSize, CancellationToken cancellationToken = default, TickerVisibility<TTimeTicker, TCronTicker> visibility = null);
        Task AddOnDemandCronTickerOccurrenceAsync(Guid id, CancellationToken cancellationToken = default);
        Task<CronTickerOccurrenceEntity<TCronTicker>[]> GetCronTickersOccurrencesAsync(Guid guid, CancellationToken cancellationToken = default);
        Task<PaginationResult<CronTickerOccurrenceEntity<TCronTicker>>> GetCronTickersOccurrencesPaginatedAsync(Guid guid, int pageNumber, int pageSize, CancellationToken cancellationToken = default);
        Task<IList<CronOccurrenceTickerGraphData>> GetCronTickersOccurrencesGraphDataAsync(Guid guid, CancellationToken cancellationToken = default);
        Task<bool> ToggleCronTickerAsync(Guid id, bool isEnabled, CancellationToken cancellationToken = default);
        bool CancelTickerById(Guid tickerId);
        Task DeleteCronTickerOccurrenceByIdAsync(Guid id, CancellationToken cancellationToken = default);
        Task<(string, int)> GetTickerRequestByIdAsync(Guid tickerId, TickerType tickerType, CancellationToken cancellationToken = default);
        IEnumerable<(string, (string, string, TickerTaskPriority))> GetTickerFunctions();
        Task<IList<(int, int)>> GetLastWeekJobStatusesAsync(CancellationToken cancellationToken = default, TickerVisibility<TTimeTicker, TCronTicker> visibility = null);
        Task<IList<(TickerStatus, int)>> GetOverallJobStatusesAsync(CancellationToken cancellationToken = default, TickerVisibility<TTimeTicker, TCronTicker> visibility = null);
        Task<IList<(string, int)>> GetMachineJobsAsync(CancellationToken cancellationToken = default, TickerVisibility<TTimeTicker, TCronTicker> visibility = null);
    }
}