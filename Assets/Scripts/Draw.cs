using UnityEngine;
using System.Collections.Generic;
using System.Linq; 
using System.IO; // <--- NEW: Required for file operations (PNG export)

public class Draw : MonoBehaviour
{
    [Header("Arduino Integration")]
    // Reference the component that reads the touch sensor data (UnoTouchSensors)
    public UnoTouchSensors colorReader; 
    
    // Reference the component that reads the position data (FeatherSenseReader)
    public FeatherSenseReader positionReader; 

    // Define the base colors associated with the sensors
    [Header("Paint Colors")]
    public Color redBase = Color.red;
    public Color yellowBase = Color.yellow;
    public Color blueBase = Color.blue;
    public Color whiteBase = Color.white;
    public Color blackBase = Color.black;
    public Color mixErrorColor = Color.magenta; 

    Color[] baseColors;

    [Header("Drawing Settings")]
    public Camera cam;
    public int totalXPixels = 1024;
    public int totalYPixels = 512;
    
    // CHANGED: brushSize is now a public float to be controlled by the potentiometer
    [Tooltip("Brush size, controlled by the potentiometer (Range 1.0 to 30.0).")] 
    public float brushSize = 4f; 
    
    [HideInInspector] 
    public Color brushColor; 
    
    public bool useInterpolation = true;
    public Transform topLeftCorner;
    public Transform bottomRightCorner;
    
    public Transform point; // The visual brush point
    
    public Material material;

    [Header("Internal")]
    public Texture2D generatedTexture;
    Color[] colorMap;
    int xPixel = 0;
    int yPixel = 0;
    bool pressedLastFrame = false;
    int lastX = 0;
    int lastY = 0;
    float xMult; 
    float yMult; 

    // World space boundaries based on the corners
    float minXWorld;
    float maxXWorld;
    float minYWorld;
    float maxYWorld;
    float baseZWorld; // Store the Z position to keep the point on the plane

    private void Start()
    {
        if (colorReader == null || positionReader == null || point == null || topLeftCorner == null || bottomRightCorner == null)
        {
            Debug.LogError("The Draw script requires all references (colorReader, positionReader, point, topLeftCorner, bottomRightCorner) to be assigned in the Inspector.");
            enabled = false;
            return;
        }

        baseColors = new Color[] { redBase, yellowBase, blueBase, whiteBase, blackBase };

        generatedTexture = new Texture2D(totalYPixels, totalXPixels, TextureFormat.RGBA32, false); 
        generatedTexture.filterMode = FilterMode.Point;
        material.SetTexture("_MainTex", generatedTexture);
 
        colorMap = new Color[totalXPixels * totalYPixels];
        ResetCanvas(); // Call the new public reset method here
 
        // Calculate World Space Ranges based on Local Position
        minXWorld = topLeftCorner.localPosition.x;
        maxXWorld = bottomRightCorner.localPosition.x;
        
        // Y position range (Vertical Axis)
        minYWorld = bottomRightCorner.localPosition.y;
        maxYWorld = topLeftCorner.localPosition.y; 

        // Lock Z-position to the plane's depth (from TopLeft Corner's Z)
        baseZWorld = topLeftCorner.localPosition.z; 

        // Multipliers for pixel calculation
        xMult = totalXPixels / (maxXWorld - minXWorld);
        yMult = totalYPixels / (maxYWorld - minYWorld);
    }
 
    private void Update()
    {
        // 1. Calculate the brush color based on sensor states
        DetermineBrushColor();
        
        // Update brushSize using the normalized potentiometer value
        UpdateBrushSize();

        // 2. Always calculate the position
        CalculatePixelFromFeatherSense();
    }
    
    // NEW FUNCTION: Updates the brush size using the potentiometer input
    void UpdateBrushSize()
    {
        if (positionReader != null)
        {
            // Assign the normalized potentiometer value (1.0 to 30.0) directly to brushSize
            brushSize = positionReader.normalizedPotentiometer;
        }
    }

    void DetermineBrushColor()
    {
        int[] states = colorReader.sensorStates;
        List<Color> activeColors = new List<Color>();

        for (int i = 0; i < states.Length; i++)
        {
            if (states[i] == 1)
            {
                activeColors.Add(baseColors[i]);
            }
        }

        int activeCount = activeColors.Count;

        if (activeCount == 0)
        {
            // No color selected, brush is transparent (color is NOT set)
            brushColor = new Color(0, 0, 0, 0); 
        }
        else if (activeCount == 1)
        {
            brushColor = activeColors[0];
        }
        else if (activeCount == 2)
        {
            brushColor = Color.Lerp(activeColors[0], activeColors[1], 0.5f);
        }
        else if (activeCount > 2)
        {
            Debug.LogError("ERROR: More than two paint sensors are activated simultaneously. Using error color.");
            brushColor = mixErrorColor;
        }
    }
 
    void CalculatePixelFromFeatherSense()
    {
        // --- NEW DRAWING TRIGGER LOGIC ---
        // A. A color must be selected (brushColor is not transparent) 
        bool colorSelected = (brushColor.a > 0.01f);
        
        // B. Distance must be less than 5cm
        bool distanceCloseEnough = (positionReader.distance_cm < 5); 
        
        // Final draw condition: must have color AND be close to the canvas
        bool shouldDraw = colorSelected && distanceCloseEnough;

        // --- Clamping and Input Assignment ---
        // normalizedX (A3: LeftRight) -> X Position
        float normX_Input = Mathf.Clamp01(positionReader.normalizedX); 
        
        // normalizedY (A0: UpDown) -> Y Position
        float normY_Input = Mathf.Clamp01(positionReader.normalizedY); 


        // --- 1. Map Normalized Arduino Data to Texture Pixels ---
        
        xPixel = Mathf.RoundToInt(normX_Input * (totalXPixels - 1));
        yPixel = Mathf.RoundToInt(normY_Input * (totalYPixels - 1));
        
        xPixel = Mathf.Clamp(xPixel, 0, totalXPixels - 1);
        yPixel = Mathf.Clamp(yPixel, 0, totalYPixels - 1);


        // --- 2. Map Normalized Arduino Data to World Space for the 'point' Transform ---
        
        float worldX = Mathf.Lerp(minXWorld, maxXWorld, normX_Input);
        float worldY = Mathf.Lerp(minYWorld, maxYWorld, normY_Input);

        // Z is locked to the plane depth (baseZWorld) as requested.
        point.localPosition = new Vector3(worldX, worldY, baseZWorld);
        
        if(shouldDraw)
        {
            // Only draw if both conditions are met
            ChangePixelsAroundPoint();
        }
        else
        {
            // Reset state if not drawing
            pressedLastFrame = false;
        }
    }
 
    void ChangePixelsAroundPoint()
    {
        if(useInterpolation && pressedLastFrame && (lastX != xPixel || lastY != yPixel))
        {
            float dx = xPixel - lastX;
            float dy = yPixel - lastY;
            int dist = (int)Mathf.Max(Mathf.Abs(dx), Mathf.Abs(dy));

            for (int i = 1; i <= dist; i++)
            {
                int interpX = (int)Mathf.Round(lastX + (dx * i) / dist);
                int interpY = (int)Mathf.Round(lastY + (dy * i) / dist);
                DrawBrush(interpX, interpY); 
            }
        }
        else
            DrawBrush(xPixel, yPixel);
        
        pressedLastFrame = true;
        lastX = xPixel;
        lastY = yPixel;
        SetTexture();
    }
 
    void DrawBrush(int xPix, int yPix)
    {
        // CAST brushSize (float) to int for loop bounds
        int brushSizeInt = Mathf.CeilToInt(brushSize); 
        
        // Use the integer brush size for array indexing and loop bounds
        int i = xPix - brushSizeInt + 1, j = yPix - brushSizeInt + 1, maxi = xPix + brushSizeInt - 1, maxj = yPix + brushSizeInt - 1; 
        
        if (i < 0) i = 0;
        if (j < 0) j = 0;
        if (maxi >= totalXPixels) maxi = totalXPixels - 1;
        if (maxj >= totalYPixels) maxj = totalYPixels - 1;
        
        for(int x=i; x<=maxi; x++)
        {
            for(int y=j; y<=maxj; y++)
            {
                // Note: The circle calculation must still use the original float brushSize 
                // for accurate radius checking, if we want sub-integer sensitivity.
                if ((x - xPix) * (x - xPix) + (y - yPix) * (y - yPix) <= brushSize * brushSize) 
                {
                    // Note: This indexing assumes the Texture2D was created as (y, x) per the original code.
                    colorMap[x * totalYPixels + y] = brushColor; 
                }
            }
        }
    }
 
    void SetTexture() 
    {
        generatedTexture.SetPixels(colorMap);
        generatedTexture.Apply();
    }
 
    /// <summary>
    /// Resets the canvas to a clean white color.
    /// This is exposed as a public method to be called by a UI Button.
    /// </summary>
    public void ResetCanvas() // <--- MADE PUBLIC
    {
        for (int i = 0; i < colorMap.Length; i++)
            colorMap[i] = Color.white;
        SetTexture();
        Debug.Log("Canvas has been reset.");
    }
    
    /// <summary>
    /// Encodes the current generated texture to PNG format and saves it to the device's persistent data path.
    /// This is exposed as a public method to be called by a UI Button.
    /// </summary>
    public void ExportCanvasToPNG() // <--- NEW PUBLIC METHOD
    {
        if (generatedTexture == null)
        {
            Debug.LogError("Texture not initialized. Cannot export.");
            return;
        }

        // 1. Encode the texture into PNG bytes
        byte[] bytes = generatedTexture.EncodeToPNG();
        
        // 2. Define the file path and name
        string fileName = $"Drawing_{System.DateTime.Now:yyyyMMdd_HHmmss}.png";
        string filePath = Path.Combine(Application.persistentDataPath, fileName);
        
        try
        {
            // 3. Write the bytes to the file system
            File.WriteAllBytes(filePath, bytes);
            Debug.Log($"Successfully exported canvas to: {filePath}");
        }
        catch (System.Exception e)
        {
            Debug.LogError($"Failed to export canvas: {e.Message}");
        }
    }
}