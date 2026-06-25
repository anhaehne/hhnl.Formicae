using System.Threading.Channels;

namespace hhnl.Formicae.Api;

public sealed class WorkflowTickNotifier
{
    private readonly Channel<bool> channel = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
        SingleReader = true,
        SingleWriter = false
    });

    public void Signal()
        => channel.Writer.TryWrite(true);

    public async Task WaitForSignalOrDelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        var delayTask = Task.Delay(delay, cancellationToken);
        var signalTask = channel.Reader.WaitToReadAsync(cancellationToken).AsTask();
        var completed = await Task.WhenAny(delayTask, signalTask);
        if (completed == signalTask && await signalTask)
        {
            while (channel.Reader.TryRead(out _))
            {
            }
        }
    }
}