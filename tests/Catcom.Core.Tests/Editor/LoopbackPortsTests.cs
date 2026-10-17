using System.Net;
using System.Net.Sockets;
using Catcom.Core.Editor;

namespace Catcom.Core.Tests.Editor;

/// <summary>Port picking for <c>nvim --listen 127.0.0.1:port</c>.</summary>
[TestFixture]
public sealed class LoopbackPortsTests
{
    private static bool CanBind(int port)
    {
        var listener = new TcpListener(IPAddress.Loopback, port) { ExclusiveAddressUse = true };
        try
        {
            listener.Start();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
        finally
        {
            listener.Stop();
        }
    }

    // Holds a listener on a port whose successor is currently free, so a two-port range has exactly
    // one bindable port (the successor).
    private static TcpListener HoldPortWithFreeSuccessor(out int held)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0) { ExclusiveAddressUse = true };
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            if (port < 65535 && CanBind(port + 1))
            {
                held = port;
                return listener;
            }

            listener.Stop();
        }

        throw new InvalidOperationException("no adjacent free loopback ports found");
    }

    [Test]
    public void PickFree_ReturnsABindablePortInsideTheRange()
    {
        var seed = LoopbackPorts.PickFree(49152, 65000);
        var min = Math.Max(seed - 20, 1024);
        var max = seed + 20;

        for (var i = 0; i < 25; i++)
        {
            var port = LoopbackPorts.PickFree(min, max);
            Assert.That(port, Is.InRange(min, max));
            Assert.That(CanBind(port), Is.True, "the probe listener is released");
        }
    }

    [Test]
    public void PickFree_SkipsAPortTheTestHolds()
    {
        using var guard = new ListenerGuard(HoldPortWithFreeSuccessor(out var held));

        // The random starting offset may land on either port; the held one must never be returned.
        for (var i = 0; i < 40; i++)
            Assert.That(LoopbackPorts.PickFree(held, held + 1), Is.EqualTo(held + 1));
    }

    [Test]
    public void PickFree_FallsBackToAnOsAssignedPortWhenTheRangeIsExhausted()
    {
        using var guard = new ListenerGuard(HoldPortWithFreeSuccessor(out var held));

        var port = LoopbackPorts.PickFree(held, held);

        Assert.That(port, Is.Not.EqualTo(held));
        Assert.That(port, Is.InRange(1, 65535));
        Assert.That(CanBind(port), Is.True);
    }

    [Test]
    public void PickFree_WithAnInvertedRange_FallsBackToAnOsAssignedPort()
    {
        var port = LoopbackPorts.PickFree(65000, 64000);

        Assert.That(port, Is.InRange(1, 65535));
        Assert.That(CanBind(port), Is.True);
    }

    private sealed class ListenerGuard(TcpListener listener) : IDisposable
    {
        public void Dispose() => listener.Stop();
    }
}
