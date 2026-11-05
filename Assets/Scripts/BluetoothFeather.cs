using System;
using System.Collections.Concurrent;
using System.IO.Ports;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

// Reads combined sensor data (5 touch states + 4 analog readings) from the
// Feather Sense.
//
// Two communication modes:
//   - Udp (default): reads lines forwarded by ble_relay.py over localhost UDP.
//     This is the wireless Bluetooth path — run ble_relay.py before pressing
//     Play so it can connect to the Feather over Bluetooth and relay its data.
//   - Serial: reads directly from a wired USB serial connection. Useful for
//     testing the firmware without the relay running.
public class BluetoothFeather : MonoBehaviour
{
    public enum CommMode { Serial, Udp }

    [Header("Communication Mode")]
    public CommMode commMode = CommMode.Udp;

    [Header("Serial Settings (wired testing only)")]
    public string portName = "/dev/cu.usbmodem1301";
    public int baudRate = 9600;
    public int readTimeoutMs = 100;

    [Header("UDP Settings (fed by ble_relay.py)")]
    [Tooltip("Must match UDP_PORT in ble_relay.py")]
    public int udpListenPort = 9000;

    [Header("Live Touch Sensor States (0 or 1)")]
    // 0: Red, 1: Yellow, 2: Blue, 3: White, 4: Black
    public int[] sensorStates = new int[5];

    [Header("Raw Analog Ranges (based on Arduino sketch)")]
    private const float A0_MIN = 560f;
    private const float A0_MAX = 930f;
    private const float A3_MIN = 0f;
    private const float A3_MAX = 920f;

    private const float A1_MIN = 0f;
    private const float A1_MAX = 900f;
    private const float NORM_A1_MIN = 1f;
    private const float NORM_A1_MAX = 30f;

    [Header("Normalized Brush Position (0.0 to 1.0)")]
    [Tooltip("X-coordinate: A3 (Left/Right)")]
    [Range(0.0f, 1.0f)]
    public float normalizedX = 0.5f;

    [Tooltip("Y-coordinate: A0 (Up/Down)")]
    [Range(0.0f, 1.0f)]
    public float normalizedY = 0.5f;

    [Tooltip("Z/Pressure: Unused for drawing logic (0.0)")]
    [Range(0.0f, 1.0f)]
    public float normalizedZ = 0.0f;

    [Header("Potentiometer Value")]
    [Tooltip("Raw A1 Potentiometer value (0-1023)")]
    public int rawPotentiometer = 0;

    [Tooltip("A1 Potentiometer value mapped from 1.0 to 30.0")]
    public float normalizedPotentiometer = 1.0f;

    // --- Serial mode state ---
    SerialPort _port;
    Thread _readerThread;
    volatile bool _runReader;

    // --- UDP mode state (fed by ble_relay.py) ---
    UdpClient _udpClient;
    Thread _udpThread;
    volatile bool _runUdp;

    readonly ConcurrentQueue<string> _lines = new ConcurrentQueue<string>();
    string _lastError = null;
    float _lastDataTime;
    bool _noDataWarned;

    void OnEnable()
    {
        _lastDataTime = Time.realtimeSinceStartup;
        _noDataWarned = false;

        if (commMode == CommMode.Serial)
            TryOpenPort();
        else
            TryOpenUdp();
    }

    void OnDisable()
    {
        StopReaderAndClose();
        StopUdp();
    }

    void Update()
    {
        if (!string.IsNullOrEmpty(_lastError))
        {
            Debug.LogError($"[BluetoothFeather] {_lastError}");
            _lastError = null;
        }

        int drained = 0;
        while (_lines.TryDequeue(out var line))
        {
            ProcessLine(line);
            _lastDataTime = Time.realtimeSinceStartup;
            _noDataWarned = false;
            drained++;
            if (drained > 50) break; // avoid starving the frame on a backlog
        }

        if (!_noDataWarned && Time.realtimeSinceStartup - _lastDataTime > 3f)
        {
            _noDataWarned = true;
            if (commMode == CommMode.Udp)
                Debug.LogWarning($"[BluetoothFeather] No data received for >3s on UDP port {udpListenPort}. Is ble_relay.py running and connected to the Feather?");
            else
                Debug.LogWarning($"[BluetoothFeather] No serial data received for >3s on '{portName}'.");
        }
    }

    void ProcessLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;

        line = line.Trim();

        if (line.StartsWith("UNITY_READY") || line.StartsWith("BLE UART ready"))
        {
            Debug.Log($"[BluetoothFeather] Device ready: {line}");
            return;
        }

        var parts = line.Split(',');

        // Combined packet = 5 touch states + 4 analog readings = 9 values.
        // [0] Red, [1] Yellow, [2] Blue, [3] White, [4] Black,
        // [5] A0 (UpDown), [6] A2 (FrontBack, unused for position),
        // [7] A3 (LeftRight), [8] A1 (Potentiometer)
        if (parts.Length == 9 && parts.All(p => int.TryParse(p.Trim(), out _)))
        {
            sensorStates[0] = int.Parse(parts[0].Trim()); // Red
            sensorStates[1] = int.Parse(parts[1].Trim()); // Yellow
            sensorStates[2] = int.Parse(parts[2].Trim()); // Blue
            sensorStates[3] = int.Parse(parts[3].Trim()); // White
            sensorStates[4] = int.Parse(parts[4].Trim()); // Black

            int rawA0 = int.Parse(parts[5].Trim());
            // parts[6] = rawA2 (FrontBack), intentionally unused, kept for parity with the firmware packet
            int rawA3 = int.Parse(parts[7].Trim());
            int rawA1 = int.Parse(parts[8].Trim());

            normalizedX = Mathf.InverseLerp(A3_MIN, A3_MAX, rawA3);
            normalizedY = Mathf.InverseLerp(A0_MIN, A0_MAX, rawA0);
            normalizedZ = 0.0f;
            rawPotentiometer = rawA1;
            normalizedPotentiometer = Remap(rawPotentiometer, A1_MIN, A1_MAX, NORM_A1_MIN, NORM_A1_MAX);
        }
        else
        {
            Debug.LogWarning($"[BluetoothFeather] Malformed data packet (expected 9 values, got {parts.Length}): {line}");
        }
    }

    float Remap(float value, float oldMin, float oldMax, float newMin, float newMax)
    {
        float normalized = (value - oldMin) / (oldMax - oldMin);
        return Mathf.Lerp(newMin, newMax, normalized);
    }

    // ---------------- Serial mode (wired testing) ----------------

    void TryOpenPort()
    {
        if (string.IsNullOrEmpty(portName))
        {
            Debug.LogError("[BluetoothFeather] No serial port specified.");
            return;
        }

        try
        {
            _port = new SerialPort(portName, baudRate);
            _port.NewLine = "\n";
            _port.ReadTimeout = readTimeoutMs;
            _port.DtrEnable = true;
            _port.RtsEnable = true;

            _port.Open();
            Debug.Log($"[BluetoothFeather] Opened serial port: {portName} @ {baudRate}");

            _runReader = true;
            _readerThread = new Thread(ReaderLoop) { IsBackground = true, Name = "BluetoothFeatherSerialReader" };
            _readerThread.Start();
        }
        catch (Exception ex)
        {
            Debug.LogError($"[BluetoothFeather] Error opening serial port '{portName}': {ex.Message}");
            SafeCloseSerial();
        }
    }

    void ReaderLoop()
    {
        try
        {
            while (_runReader && _port != null && _port.IsOpen)
            {
                try
                {
                    string line = _port.ReadLine();
                    if (!string.IsNullOrWhiteSpace(line))
                        _lines.Enqueue(line);
                }
                catch (TimeoutException) { }
                catch (Exception ex)
                {
                    _lastError = $"Serial read error: {ex.Message}";
                    break;
                }
            }
        }
        finally { }
    }

    void StopReaderAndClose()
    {
        _runReader = false;
        if (_readerThread != null)
        {
            try { _readerThread.Join(500); } catch { }
            _readerThread = null;
        }
        SafeCloseSerial();
    }

    void SafeCloseSerial()
    {
        if (_port != null)
        {
            try
            {
                if (_port.IsOpen) _port.Close();
            }
            catch { }
            finally
            {
                _port.Dispose();
                _port = null;
            }
        }
    }

    // ---------------- UDP mode (fed by ble_relay.py over Bluetooth) ----------------

    void TryOpenUdp()
    {
        try
        {
            _udpClient = new UdpClient(udpListenPort);
            Debug.Log($"[BluetoothFeather] Listening for BLE relay data on 127.0.0.1:{udpListenPort}");

            _runUdp = true;
            _udpThread = new Thread(UdpReaderLoop) { IsBackground = true, Name = "BluetoothFeatherUdpReader" };
            _udpThread.Start();
        }
        catch (Exception ex)
        {
            Debug.LogError($"[BluetoothFeather] Failed to start UDP listener on port {udpListenPort}: {ex.Message}");
            _udpClient = null;
        }
    }

    void UdpReaderLoop()
    {
        var endpoint = new IPEndPoint(IPAddress.Any, 0);
        var decoder = Encoding.UTF8;

        while (_runUdp)
        {
            byte[] bytes;
            try
            {
                bytes = _udpClient.Receive(ref endpoint); // blocks until a packet arrives or the socket closes
            }
            catch (ObjectDisposedException)
            {
                break; // socket closed on stop
            }
            catch (SocketException)
            {
                continue;
            }

            var chunk = decoder.GetString(bytes);
            foreach (var part in chunk.Split('\n'))
            {
                var line = part.TrimEnd('\r');
                if (!string.IsNullOrEmpty(line))
                    _lines.Enqueue(line);
            }
        }
    }

    void StopUdp()
    {
        _runUdp = false;
        try { _udpClient?.Close(); } catch { }
        try { _udpClient?.Dispose(); } catch { }
        _udpClient = null;
        if (_udpThread != null)
        {
            try { _udpThread.Join(300); } catch { }
            _udpThread = null;
        }
    }
}