using UnityEngine;
using System.IO.Ports;
using System;
using System.Threading;

public class FeatherSenseReader : MonoBehaviour
{
    [Header("Serial Port Settings")]
    public string portName = "/dev/cu.usbmodem1201"; 
    public int baudRate = 9600;

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

    [Header("Ultrasonic Distance")]
    [Tooltip("Distance from the SR04 sensor in centimeters.")]
    public int distance_cm = 999; 

    [Header("Potentiometer Value")] 
    [Tooltip("Raw A1 Potentiometer value (0-1023)")]
    public int rawPotentiometer = 0; 
    
    [Tooltip("A1 Potentiometer value mapped from 1.0 to 30.0")] 
    public float normalizedPotentiometer = 1.0f; 
    
    private SerialPort serialPort;
    private bool isRunning = false;
    private Thread readThread;

    private volatile string latestDataPacket = null;
    
    private void Start()
    {
        OpenSerialPort();
        
        isRunning = true;
        readThread = new Thread(ReadSerialData);
        readThread.IsBackground = true; 
        readThread.Start();
    }
    
    private void Update()
    {
        if (latestDataPacket != null)
        {
            string dataToProcess = latestDataPacket;
            latestDataPacket = null; 
            ProcessData(dataToProcess);
        }
    }

    private void OpenSerialPort()
    {
        try
        {
            serialPort = new SerialPort(portName, baudRate);
            serialPort.ReadTimeout = 1; 
            serialPort.Open();
            Debug.Log($"Serial Port {portName} opened successfully at {baudRate} baud.");
        }
        catch (Exception e)
        {
            Debug.LogError($"Could not open serial port {portName}: {e.Message}");
            enabled = false;
        }
    }

    private void ReadSerialData()
    {
        while (isRunning && serialPort != null && serialPort.IsOpen)
        {
            try
            {
                string data = serialPort.ReadLine();
                if (data.StartsWith("UNITY_READY")) 
                {
                    Debug.Log($"Arduino Ready: {data}");
                    continue;
                }
                
                latestDataPacket = data; 
            }
            catch (TimeoutException)
            {
            }
            catch (Exception)
            {
            }
            Thread.Sleep(5); 
        }
    }

    private void ProcessData(string data)
    {
        string[] values = data.Split(',');
        
        if (values.Length == 5) 
        {
            if (int.TryParse(values[0].Trim(), out int rawA0) &&         // 0: UpDown
                int.TryParse(values[1].Trim(), out int rawA2) &&         // 1: FrontBack (Ignored for position)
                int.TryParse(values[2].Trim(), out int rawA3) &&         // 2: LeftRight
                int.TryParse(values[3].Trim(), out int rawDistance) &&   // 3: Distance
                int.TryParse(values[4].Trim(), out int rawA1))           // 4: Potentiometer
            {
                normalizedX = Mathf.InverseLerp(A3_MIN, A3_MAX, rawA3);
                normalizedY = Mathf.InverseLerp(A0_MIN, A0_MAX, rawA0);
                normalizedZ = 0.0f;                 
                distance_cm = rawDistance;
                rawPotentiometer = rawA1;
                normalizedPotentiometer = Remap(rawPotentiometer, A1_MIN, A1_MAX, NORM_A1_MIN, NORM_A1_MAX);
            }
        }
        else
        {
            Debug.LogWarning($"Received malformed data packet (Expected 5 values, got {values.Length}): {data}");
        }
    }
    private float Remap(float value, float oldMin, float oldMax, float newMin, float newMax)
    {
        float normalized = (value - oldMin) / (oldMax - oldMin);
        return Mathf.Lerp(newMin, newMax, normalized);
    }


    private void OnApplicationQuit()
    {
        isRunning = false;
        
        if (readThread != null && readThread.IsAlive)
        {
            readThread.Join();
        }

        if (serialPort != null && serialPort.IsOpen)
        {
            serialPort.Close();
            serialPort.Dispose();
            Debug.Log("Serial Port closed.");
        }
    }
}