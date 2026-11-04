using System.Threading.Channels;

namespace Invex.Passel.WebApi.Services;

internal interface IParcelSweepSignal
{
    /// <summary>
    ///     Wakes the sweep, after a commit that marked a parcel deleted, so its bytes are erased within seconds rather
    ///     than at the next interval. Never waits.
    /// </summary>
    void Poke();
}

/// <summary>
///     The sweep's wake-up call on this instance. It holds at most one pending poke: the sweep erases every deleted
///     parcel it finds when it runs, so pokes that arrive while one is pending add nothing.
/// </summary>
internal sealed class ParcelSweepSignal : IParcelSweepSignal
{
    private readonly Channel<bool> _channel = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
    });

    internal ChannelReader<bool> Reader =>
        _channel.Reader;

    public void Poke() =>
        _channel.Writer.TryWrite(true);
}
