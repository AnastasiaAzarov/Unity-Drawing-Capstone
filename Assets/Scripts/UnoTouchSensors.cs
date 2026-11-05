using System;
using System.Collections.Concurrent;
using System.IO.Ports;
using System.Threading;
using UnityEngine;
using System.Linq;

public class UnoTouchSensors : MonoBehaviour
{
    [Header("Serial Settings")]
    public string portName = "/dev/cu.usbmodem1301"; 
    public int baudRate = 9600;
    public int readTimeoutMs = 100;

    [Header("Live Sensor States (0 or 1)")]
    public int[] sensorStates = new int[5];

    SerialPort _port;
    Thread _readerThread;
    volatile bool _runReader;
    readonly ConcurrentQueue<string> _lines = new ConcurrentQueue<string>();
    string _lastError = null;

    void OnEnable()
    {
        TryOpenPort();
    }

    void OnDisable()
    {
        StopReaderAndClose();
    }

    void Update()
    {
        if (!string.IsNullOrEmpty(_lastError))
        {
            Debug.LogError($"[Arduino Color Reader] {_lastError}");
            _lastError = null;
        }

        while (_lines.TryDequeue(out var line))
        {
            var parts = line.Split(',');
            
            if (parts.Length == 5 && parts.All(p => int.TryParse(p.Trim(), out _)))
            {
                sensorStates[0] = int.Parse(parts[0].Trim()); // Red
                sensorStates[1] = int.Parse(parts[1].Trim()); // Yellow
                sensorStates[2] = int.Parse(parts[2].Trim()); // Blue
                sensorStates[3] = int.Parse(parts[3].Trim()); // White
                sensorStates[4] = int.Parse(parts[4].Trim()); // Black
            }
        }
    }

    void TryOpenPort()
    {
        if (string.IsNullOrEmpty(portName))
        {
            Debug.LogError("[Arduino Color Reader] No serial port specified.");
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
            Debug.Log($"[Arduino Color Reader] Opened serial port: {portName} @ {baudRate}");

            _runReader = true;
            _readerThread = new Thread(ReaderLoop) { IsBackground = true, Name = "ColorSerialReader" };
            _readerThread.Start();
        }
        catch (Exception ex)
        {
            Debug.LogError($"[Arduino Color Reader] Error opening serial port '{portName}': {ex.Message}");
            SafeClose();
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
        SafeClose();
    }

    void SafeClose()
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
}
