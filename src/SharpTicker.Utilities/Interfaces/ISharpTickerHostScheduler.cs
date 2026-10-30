using System;
using System.Threading;
using System.Threading.Tasks;

namespace SharpTicker.Utilities.Interfaces
{
    public interface ISharpTickerHostScheduler
    {
        public bool IsRunning { get; }
        Task StartAsync(CancellationToken cancellationToken = default);
        Task StopAsync(CancellationToken cancellationToken = default);
        void RestartIfNeeded(DateTime? dateTime);
        void Restart();
    }
}
