using System;
using System.Threading.Tasks;
using SharpTicker.Utilities.Enums;

namespace SharpTicker.Utilities.Interfaces
{
    public interface ITickerExceptionHandler
    {
        public Task HandleExceptionAsync(Exception exception, Guid tickerId, TickerType tickerType);
        public Task HandleCanceledExceptionAsync(Exception exception, Guid tickerId, TickerType tickerType);
    }
}
