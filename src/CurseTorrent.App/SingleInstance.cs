using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CurseTorrent.App;

internal sealed class SingleInstance : IDisposable
{
    private const int MaximumMessageBytes = 2 * 1024 * 1024;
    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _stop = new();
    private Task? _server;
    internal bool IsPrimary { get; }

    internal SingleInstance()
    {
        string user = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        string suffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(user)))[..20];
        _pipeName = "CurseTorrent." + suffix + "." + Process.GetCurrentProcess().SessionId;
        _mutex = new Mutex(true, @"Local\" + _pipeName, out bool created);
        IsPrimary = created;
    }

    internal void StartServer(Action<string[]> received)
    {
        if (!IsPrimary) throw new InvalidOperationException("Only the primary process can listen.");
        _server = Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    using var pipe = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await pipe.WaitForConnectionAsync(_stop.Token);
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                    deadline.CancelAfter(TimeSpan.FromSeconds(10));
                    var header = new byte[4];
                    await pipe.ReadExactlyAsync(header, deadline.Token);
                    int count = BinaryPrimitives.ReadInt32LittleEndian(header);
                    if (count < 2 || count > MaximumMessageBytes) continue;
                    var payload = new byte[count];
                    await pipe.ReadExactlyAsync(payload, deadline.Token);
                    var sources = JsonSerializer.Deserialize<string[]>(payload) ?? [];
                    if (sources.Length > 64 || sources.Any(source => source is null || source.Length > 32768)) continue;
                    received(sources);
                    await pipe.WriteAsync(new byte[] { 1 }, deadline.Token);
                    await pipe.FlushAsync(deadline.Token);
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
                catch (Exception exception) when (exception is IOException or OperationCanceledException or JsonException or UnauthorizedAccessException)
                { if (!_stop.IsCancellationRequested) App.WriteLog(exception); }
            }
        });
    }

    internal async Task ForwardAsync(string[] sources)
    {
        if (sources.Length > 64 || sources.Any(source => source.Length > 32768)) throw new ArgumentException("Слишком много файлов или слишком длинная ссылка.");
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(sources);
        if (payload.Length > MaximumMessageBytes) throw new ArgumentException("Слишком большой запрос к приложению.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        using var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(10000, timeout.Token);
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await pipe.WriteAsync(header, timeout.Token);
        await pipe.WriteAsync(payload, timeout.Token);
        await pipe.FlushAsync(timeout.Token);
        var acknowledgement = new byte[1];
        await pipe.ReadExactlyAsync(acknowledgement, timeout.Token);
        if (acknowledgement[0] != 1) throw new IOException("Приложение не приняло торрент.");
    }

    public void Dispose()
    {
        _stop.Cancel();
        if (IsPrimary)
        {
            try { _mutex.ReleaseMutex(); }
            catch (ApplicationException) { }
        }
        _mutex.Dispose();
        // The server owns its pipe and unwinds asynchronously when cancellation fires.
    }
}
