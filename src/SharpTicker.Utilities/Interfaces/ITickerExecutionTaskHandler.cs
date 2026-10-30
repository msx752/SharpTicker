using System.Threading;
using System.Threading.Tasks;
using SharpTicker.Utilities.Models;

namespace SharpTicker.Utilities.Interfaces
{
    public interface ITickerExecutionTaskHandler
    {
        Task ExecuteTaskAsync(InternalFunctionContext context, bool isDue, CancellationToken cancellationToken = default);
    }
}

