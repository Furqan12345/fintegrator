using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Application.Models;

namespace SimpleIPaaS.Application.Services;

public class ChannelExecutionQueue : IExecutionQueue
{
    private readonly Channel<ExecutionRequest> _channel;

    public ChannelExecutionQueue(IOptions<ExecutionOptions> options)
    {
        var capacity = options.Value.QueueCapacity < 1 ? 100 : options.Value.QueueCapacity;
        _channel = Channel.CreateBounded<ExecutionRequest>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false
        });
    }

    public ValueTask EnqueueAsync(ExecutionRequest request, CancellationToken cancellationToken = default)
        => _channel.Writer.WriteAsync(request, cancellationToken);

    public ValueTask<ExecutionRequest> DequeueAsync(CancellationToken cancellationToken)
        => _channel.Reader.ReadAsync(cancellationToken);
}
