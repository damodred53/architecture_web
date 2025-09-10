using System.Threading.Channels;

namespace ApiElasticSearch.Worker;

public interface IBackgroundTaskQueue
{
    void QueueBackgroundWorkItem(Func<CancellationToken, Task> workItem);
    IAsyncEnumerable<Func<CancellationToken, Task>> DequeueAsync(CancellationToken ct);
}

public sealed class BackgroundTaskQueue : IBackgroundTaskQueue
{
    private readonly Channel<Func<CancellationToken, Task>> _queue =
        Channel.CreateUnbounded<Func<CancellationToken, Task>>();

    public void QueueBackgroundWorkItem(Func<CancellationToken, Task> workItem)
        => _queue.Writer.TryWrite(workItem);

    public IAsyncEnumerable<Func<CancellationToken, Task>> DequeueAsync(CancellationToken ct)
        => _queue.Reader.ReadAllAsync(ct);
}