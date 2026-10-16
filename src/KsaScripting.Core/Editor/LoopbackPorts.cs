using System.Net;
using System.Net.Sockets;

namespace KsaScripting.Core.Editor;

/// <summary>Picks free TCP ports on the loopback interface for <c>nvim --listen 127.0.0.1:&lt;port&gt;</c>.</summary>
public static class LoopbackPorts
{
    /// <summary>
    ///     Returns the first bindable loopback port in <paramref name="min"/>..<paramref name="max"/>
    ///     (inclusive), scanning from a random offset so concurrent editors do not collide. The probe
    ///     listener is stopped before returning. Falls back to an OS-assigned port when the range is
    ///     empty, invalid or fully taken.
    /// </summary>
    /// <param name="min">Lowest acceptable port (clamped to 1).</param>
    /// <param name="max">Highest acceptable port (clamped to 65535).</param>
    /// <returns>A port that was bindable a moment ago.</returns>
    public static int PickFree(int min, int max)
    {
        min = Math.Max(min, 1);
        max = Math.Min(max, 65535);
        if (min <= max)
        {
            var count = max - min + 1;
            var start = Random.Shared.Next(count);
            for (var i = 0; i < count; i++)
            {
                var port = min + ((start + i) % count);
                if (TryBind(port) is not null)
                    return port;
            }
        }

        return TryBind(0) ?? throw new InvalidOperationException("The OS did not assign a loopback port.");
    }

    // Returns the bound port (the OS-assigned one for 0), or null when it cannot be bound.
    private static int? TryBind(int port)
    {
        var listener = new TcpListener(IPAddress.Loopback, port) { ExclusiveAddressUse = true };
        try
        {
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        catch (SocketException)
        {
            return null;
        }
        finally
        {
            listener.Stop();
        }
    }
}
