using System.IO.Ports;
using Microsoft.Win32;
using FT857DControl.Models;

namespace FT857DControl.Services;

public sealed class RadioConnection : IDisposable
{
    private SerialPort? _port;
    private readonly SemaphoreSlim _gate = new(1, 1);
    public bool IsOpen => _port?.IsOpen == true;

    public static string[] AvailablePorts()
    {
        var ports = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var port in SerialPort.GetPortNames())
                if (!string.IsNullOrWhiteSpace(port)) ports.Add(port.Trim());
        }
        catch { /* Registry fallback below handles machines where GetPortNames fails. */ }

        // Windows fallback: some USB CAT drivers appear in SERIALCOMM before
        // System.IO.Ports sees them (notably after driver install/replug).
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DEVICEMAP\SERIALCOMM");
            if (key != null)
            {
                foreach (var name in key.GetValueNames())
                    if (key.GetValue(name) is string port && !string.IsNullOrWhiteSpace(port))
                        ports.Add(port.Trim());
            }
        }
        catch { /* Keep any ports already found. */ }

        return ports.OrderBy(PortSortKey).ThenBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static int PortSortKey(string port) =>
        port.StartsWith("COM", StringComparison.OrdinalIgnoreCase) &&
        int.TryParse(port.AsSpan(3), out var n) ? n : int.MaxValue;

    public void Open(string portName, int baud)
    {
        Close();
        _port = new SerialPort(portName, baud, Parity.None, 8, StopBits.Two)
        {
            Handshake = Handshake.None,
            ReadTimeout = 1800,
            WriteTimeout = 1800,
            DtrEnable = false,
            RtsEnable = false
        };
        _port.Open();
        _port.DiscardInBuffer();
        _port.DiscardOutBuffer();
        // Give older USB/TTL CAT interfaces time to settle after opening the COM port.
        Thread.Sleep(150);
    }

    public void Close()
    {
        if (_port is { IsOpen: true }) _port.Close();
        _port?.Dispose();
        _port = null;
    }

    public async Task SendAsync(byte[] command, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            EnsureOpen();
            await WriteCatBlockAsync(command, ct);
            await Task.Delay(25, ct);
        }
        finally { _gate.Release(); }
    }

    public async Task<RadioState> ReadStateAsync(CancellationToken ct = default)
    {
        var data = await ExchangeAsync(CatProtocol.ReadFrequencyAndMode(), 5, ct);
        return CatProtocol.ParseFrequencyAndMode(data);
    }

    public async Task<byte> ReadRxStatusAsync(CancellationToken ct = default) =>
        (await ExchangeAsync(CatProtocol.ReadRxStatus(), 1, ct))[0];

    public async Task<byte> ReadTxStatusAsync(CancellationToken ct = default) =>
        (await ExchangeAsync(CatProtocol.ReadTxStatus(), 1, ct))[0];

    // The FT-817/818 project used undocumented EEPROM addresses for internal memories.
    // Those addresses are not documented for the FT-857D, so this build deliberately
    // blocks EEPROM access rather than risk reading/writing unintended radio data.
    public Task<byte[]> ReadEepromAsync(ushort address, int byteCount, CancellationToken ct = default) =>
        throw new NotSupportedException("Direct FT-857D EEPROM memory access is disabled for safety. Use the controller memories instead.");

    private async Task<byte[]> ExchangeAsync(byte[] command, int count, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            EnsureOpen();
            _port!.DiscardInBuffer();
            await WriteCatBlockAsync(command, ct);
            return await Task.Run(() =>
            {
                var buffer = new byte[count];
                var offset = 0;
                while (offset < count)
                {
                    ct.ThrowIfCancellationRequested();
                    offset += _port.Read(buffer, offset, count - offset);
                }
                return buffer;
            }, ct);
        }
        finally { _gate.Release(); }
    }


    private async Task WriteCatBlockAsync(byte[] command, CancellationToken ct)
    {
        EnsureOpen();
        // FT-857D CAT permits up to 200 ms between bytes. Sending the five-byte
        // block with a small gap is friendlier to many CT-62/USB TTL adapters
        // than relying on a driver to flush the whole block at once.
        for (var i = 0; i < command.Length; i++)
        {
            _port!.Write(command, i, 1);
            if (i + 1 < command.Length) await Task.Delay(8, ct);
        }
    }

    private void EnsureOpen()
    {
        if (!IsOpen) throw new InvalidOperationException("The radio is not connected.");
    }

    public void Dispose() { Close(); _gate.Dispose(); }
}
