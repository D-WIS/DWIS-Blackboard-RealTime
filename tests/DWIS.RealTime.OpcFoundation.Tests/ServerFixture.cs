using System.Net;
using System.Net.Sockets;
using DWIS.RealTime.OpcFoundation;

namespace DWIS.RealTime.OpcFoundation.Tests;

/// <summary>One in-process server per test class, on a free port, with its certificate stores in a temporary folder.</summary>
public sealed class ServerFixture : IAsyncLifetime
{
    private string? _pkiRoot;

    /// <summary>The endpoint URL of the server.</summary>
    public string EndpointUrl { get; private set; } = "";

    /// <summary>The running server host.</summary>
    public ServerHost Host { get; private set; } = null!;

    /// <summary>The adapter.</summary>
    public BlackboardNodeManager NodeManager => Host.NodeManager;

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        _pkiRoot = Path.Combine(Path.GetTempPath(), "dwis-realtime-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_pkiRoot);
        EndpointUrl = $"opc.tcp://localhost:{FreePort()}";
        Host = await ServerHost.StartAsync(EndpointUrl, _pkiRoot);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Host is not null)
        {
            await Host.DisposeAsync();
        }
        if (_pkiRoot is not null)
        {
            try { Directory.Delete(_pkiRoot, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
