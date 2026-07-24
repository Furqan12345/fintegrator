using System.Threading;
using System.Threading.Tasks;
using SimpleIPaaS.Application.Models;

namespace SimpleIPaaS.Application.Interfaces;

public interface IExecutionQueue
{
    ValueTask EnqueueAsync(ExecutionRequest request, CancellationToken cancellationToken = default);
    ValueTask<ExecutionRequest> DequeueAsync(CancellationToken cancellationToken);
}
