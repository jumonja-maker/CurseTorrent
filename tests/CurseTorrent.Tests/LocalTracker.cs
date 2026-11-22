using System.Net;
using System.Net.Sockets;
using MonoTorrent;
using MonoTorrent.Connections.TrackerServer;
using MonoTorrent.TrackerServer;

/// <summary>A real loopback UDP tracker; no URL ACL, administrator rights, or public network.</summary>
internal sealed class LocalTracker : IDisposable
{
    private readonly TrackerServer _server;
    private readonly ITrackerListener _listener;

    public string AnnounceUrl { get; }

    public LocalTracker()
    {
        using var reservation = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)reservation.Client.LocalEndPoint!).Port;
        reservation.Close();
        AnnounceUrl = $"udp://127.0.0.1:{port}/announce";
        _server = new TrackerServer
        {
            AllowUnregisteredTorrents = true,
            AnnounceInterval = TimeSpan.FromSeconds(1),
            MinAnnounceInterval = TimeSpan.FromSeconds(1)
        };
        _listener = TrackerListenerFactory.CreateUdp(IPAddress.Loopback, port);
        _server.RegisterListener(_listener);
        _listener.Start();
    }

    public bool HasPeer(Torrent torrent) => _server.GetTrackerItem(new InfoHashTrackable(torrent))?.Count > 0;

    public string Magnet(Torrent torrent) =>
        "magnet:?xt=urn:btih:" + torrent.InfoHashes.V1!.ToHex() + "&tr=" + Uri.EscapeDataString(AnnounceUrl);

    public void Dispose()
    {
        _listener.Stop();
        _listener.Dispose();
        _server.Dispose();
    }
}
