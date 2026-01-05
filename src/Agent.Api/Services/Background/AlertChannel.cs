using System.Threading.Channels;

namespace Agent.Api.Services.Background;

public record AlertContext(string RuleName, bool IsHybrid);

public class AlertChannel
{
    private readonly Channel<AlertContext> _channel;

    public AlertChannel()
    {
        var options = new BoundedChannelOptions(100)
        {
            FullMode = BoundedChannelFullMode.Wait
        };
        _channel = Channel.CreateBounded<AlertContext>(options);
    }

    public ValueTask WriteAsync(AlertContext context, CancellationToken ct = default) 
        => _channel.Writer.WriteAsync(context, ct);

    public IAsyncEnumerable<AlertContext> ReadAllAsync(CancellationToken ct = default) 
        => _channel.Reader.ReadAllAsync(ct);
}
