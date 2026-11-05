using System;
using System.Collections.Concurrent;
using System.IO.Ports;
using System.Threading;
using UnityEngine;

/// <summary>
/// Reads "A1,A2\n" lines (two 10-bit potentiometer values, 0-1023) from the Arduino
/// and exposes them as normalized 0..1 values.
/// A1 = left knob  -> X axis
/// A2 = right knob -> Y axis
/// </summary>
public class SketchAEtchUno : MonoBehaviour
{
    [Header("Serial Settings")]
    public string portName = "/dev/cu.usbmodem1301";
    public int baudRate = 9600;
    public int readTimeoutMs = 100;

    [Header("Potentiometer Settings")]
    [Tooltip("Max raw value from analogRead (1023 for a 10-bit Uno ADC).")]
    public int adcMax = 1023;
    public bool invertX = false;
    public bool invertY = false;
    [Tooltip("0 = no smoothing (raw). Higher = smoother but laggier (0 to 0.95).")]
    [Range(0f, 0.95f)] public float smoothing = 0.5f;

    [Header("Live Values (read-only)")]
    public int rawLeft;    // A1
    public int rawRight;   // A2
    [Range(0f, 1f)] public float normalizedX;
    [Range(0f, 1f)] public float normalizedY;

    SerialPort _port;
    Thread _readerThread;
    volatile bool _runReader;
    readonly ConcurrentQueue<string> _lines = new ConcurrentQueue<string>();
    volatile string _lastError = null;
    bool _hasFirstSample = false;

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
            Debug.LogError($"[Arduino Pot Reader] {_lastError}");
            _lastError = null;
        }

        while (_lines.TryDequeue(out var line))
        {
            // Expected format: "512,730". Anything else (e.g. "Ready: A1, A2") is ignored.
            var parts = line.Split(',');
            if (parts.Length != 2) continue;
            if (!int.TryParse(parts[0].Trim(), out int a1)) continue;
            if (!int.TryParse(parts[1].Trim(), out int a2)) continue;

            rawLeft = Mathf.Clamp(a1, 0, adcMax);
            rawRight = Mathf.Clamp(a2, 0, adcMax);

            float targetX = rawLeft / (float)adcMax;
            float targetY = rawRight / (float)adcMax;
            if (invertX) targetX = 1f - targetX;
            if (invertY) targetY = 1f - targetY;

            if (!_hasFirstSample || smoothing <= 0f)
            {
                normalizedX = targetX;
                normalizedY = targetY;
                _hasFirstSample = true;
            }
            else
            {
                // Exponential smoothing to reduce potentiometer jitter.
                normalizedX = Mathf.Lerp(targetX, normalizedX, smoothing);
                normalizedY = Mathf.Lerp(targetY, normalizedY, smoothing);
            }
        }
    }

    void TryOpenPort()
    {
        if (string.IsNullOrEmpty(portName))
        {
            Debug.LogError("[Arduino Pot Reader] No serial port specified.");
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
            Debug.Log($"[Arduino Pot Reader] Opened serial port: {portName} @ {baudRate}");

            _runReader = true;
            _readerThread = new Thread(ReaderLoop) { IsBackground = true, Name = "PotSerialReader" };
            _readerThread.Start();
        }
        catch (Exception ex)
        {
            Debug.LogError($"[Arduino Pot Reader] Error opening serial port '{portName}': {ex.Message}");
            SafeClose();
        }
    }

    void ReaderLoop()
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