using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using ForzaHaptics.Util;

namespace ForzaHaptics.Telemetry;

/// <summary>
/// Receives FH6 Data Out packets over UDP on a separate thread.
/// Optionally forwards raw packets to other applications and writes them to a file.
/// </summary>
public sealed class UdpTelemetryReceiver : IDisposable
{
    private readonly UdpClient _udp;
    private readonly UdpClient? _forwarder;
    private readonly List<IPEndPoint> _forwardTargets = new();
    private readonly TelemetryRecorder? _recorder;
    private readonly Action<ForzaPacket> _onPacket;
    private readonly Action _onIdle;
    private readonly Thread _thread;
    private volatile bool _running = true;
    private long _received, _invalid;

    public int Port { get; }
    public long Received => Interlocked.Read(ref _received);
    public long Invalid => Interlocked.Read(ref _invalid);

    public UdpTelemetryReceiver(int port, IEnumerable<string> forwardTo, TelemetryRecorder? recorder,
        Action<ForzaPacket> onPacket, Action onIdle)
    {
        Port = port;
        _recorder = recorder;
        _onPacket = onPacket;
        _onIdle = onIdle;

        foreach (string target in forwardTo)
        {
            if (IPEndPoint.TryParse(target, out var ep) && ep.Port != 0)
            {
                if (ep.Port == port && (IPAddress.IsLoopback(ep.Address) || ep.Address.Equals(IPAddress.Any)))
                {
                    Log.Warn($"ForwardTo {target} is this application's own port; skipping it");
                    continue;
                }
                _forwardTargets.Add(ep);
            }
            else
            {
                Log.Warn($"ForwardTo: could not parse \"{target}\" (expected IP:port)");
            }
        }
        _udp = new UdpClient(AddressFamily.InterNetwork);
        try
        {
            if (_forwardTargets.Count > 0) _forwarder = new UdpClient(AddressFamily.InterNetwork);
            _udp.Client.Bind(new IPEndPoint(IPAddress.Any, port)); // SocketException if the port is busy
            Port = ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;
            _udp.Client.ReceiveTimeout = 100;

            _thread = new Thread(Loop) { IsBackground = true, Name = "FH6 telemetry", Priority = ThreadPriority.AboveNormal };
            _thread.Start();
        }
        catch
        {
            // A failed constructor cannot be disposed by the engine that requested it.
            _udp.Dispose();
            _forwarder?.Dispose();
            throw;
        }
    }

    private void Loop()
    {
        var remote = new IPEndPoint(IPAddress.Any, 0);
        while (_running)
        {
            try
            {
                byte[] data;
                try
                {
                    data = _udp.Receive(ref remote);
                }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.TimedOut)
                {
                    continue;
                }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
                {
                    continue; // Windows: ICMP "port unreachable" from a previous send
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (SocketException ex)
                {
                    if (!_running) break;
                    Log.Warn($"UDP: {ex.Message}");
                    continue;
                }

                if (_forwarder != null)
                {
                    foreach (var target in _forwardTargets)
                    {
                        try { _forwarder.Send(data, data.Length, target); } catch { /* the other application is not listening; harmless */ }
                    }
                }

                _recorder?.Write(data);

                if (ForzaPacket.TryParse(data, out var packet))
                {
                    Interlocked.Increment(ref _received);
                    try
                    {
                        _onPacket(packet);
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"Packet processing error: {ex.Message}");
                    }
                }
                else
                {
                    if (Interlocked.Increment(ref _invalid) == 1)
                        Log.Warn($"Received a {data.Length}-byte packet from {remote}; invalid FH6 telemetry (expected {ForzaPacket.MinSize} or {ForzaPacket.Size} bytes and finite feedback values).");
                }
            }
            catch (Exception ex)
            {
                if (_running) Log.Error("Telemetry receive error: " + ex.Message);
            }
            finally
            {
                if (_running)
                {
                    try { _onIdle(); }
                    catch (Exception ex) { Log.Error($"Telemetry watchdog error: {ex.Message}"); }
                }
            }
        }
    }

    public void Dispose()
    {
        _running = false;
        _udp.Dispose();
        _forwarder?.Dispose();
        if (_thread.IsAlive) _thread.Join(500);
    }
}

/// <summary>Records raw packets to a file for replay and tuning without the game.</summary>
public sealed class TelemetryRecorder : IDisposable
{
    internal static readonly byte[] Magic = "FHREC1\0\0"u8.ToArray();

    private readonly BinaryWriter _writer;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly object _sync = new();
    private bool _disposed;

    public string Path { get; }
    public int Count { get; private set; }

    public TelemetryRecorder(string path)
    {
        Path = System.IO.Path.GetFullPath(path);
        _writer = new BinaryWriter(File.Create(Path));
        _writer.Write(Magic);
    }

    public void Write(ReadOnlySpan<byte> packet)
    {
        lock (_sync)
        {
            if (_disposed) return;
            _writer.Write(_clock.Elapsed.TotalSeconds);
            _writer.Write((ushort)packet.Length);
            _writer.Write(packet);
            Count++;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _writer.Dispose();
        }
    }
}

public static class TelemetryRecording
{
    public static List<(double Time, byte[] Data)> Load(string path)
    {
        var result = new List<(double, byte[])>();
        using var reader = new BinaryReader(File.OpenRead(path));
        var magic = reader.ReadBytes(TelemetryRecorder.Magic.Length);
        if (!magic.AsSpan().SequenceEqual(TelemetryRecorder.Magic))
            throw new InvalidDataException("This is not a ForzaHaptics recording file (.fhrec)");

        while (reader.BaseStream.Position < reader.BaseStream.Length)
        {
            double time = reader.ReadDouble();
            int length = reader.ReadUInt16();
            byte[] data = reader.ReadBytes(length);
            if (data.Length < length) break; // truncated tail
            result.Add((time, data));
        }
        return result;
    }
}
