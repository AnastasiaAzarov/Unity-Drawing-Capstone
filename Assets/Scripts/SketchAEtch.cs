using UnityEngine;
using System.IO;

/// <summary>
/// Etch-A-Sketch style drawing: the left potentiometer (A1) moves the cursor along X,
/// the right potentiometer (A2) moves it along Y. Color and brush size are set in the
/// Inspector, since the Arduino now only sends the two potentiometer values.
/// Keyboard: Space = hold to pause drawing (pen up) if penAlwaysDown is off,
///           R = reset canvas, E = export PNG.
/// </summary>
public class Draw : MonoBehaviour
{
    [Header("Arduino Integration")]
    public SketchAEtchUno sensorReader;

    [Header("Drawing Settings")]
    public Camera cam;
    public int totalXPixels = 1024;
    public int totalYPixels = 512;

    // Hardcoded brush settings
    const float brushSize = 4f;                 // brush radius in pixels
    static readonly Color brushColor = Color.black;
    static readonly Color canvasColor = Color.white;

    [Tooltip("If true the pen is always down (classic Etch-A-Sketch). If false, hold Space to draw.")]
    public bool penAlwaysDown = true;

    public bool useInterpolation = true;
    public Transform topLeftCorner;
    public Transform bottomRightCorner;

    public Transform point;

    public Material material;

    [Header("Keyboard Shortcuts")]
    public KeyCode drawKey = KeyCode.Space;
    public KeyCode resetKey = KeyCode.R;
    public KeyCode exportKey = KeyCode.E;

    [Header("Internal")]
    public Texture2D generatedTexture;
    Color[] colorMap;
    int xPixel = 0;
    int yPixel = 0;
    bool pressedLastFrame = false;
    int lastX = 0;
    int lastY = 0;

    float minXWorld;
    float maxXWorld;
    float minYWorld;
    float maxYWorld;
    float baseZWorld;

    private void Start()
    {
        if (sensorReader == null || point == null || topLeftCorner == null || bottomRightCorner == null)
        {
            Debug.LogError("The Draw script requires all references (sensorReader, point, topLeftCorner, bottomRightCorner) to be assigned in the Inspector.");
            enabled = false;
            return;
        }

        generatedTexture = new Texture2D(totalYPixels, totalXPixels, TextureFormat.RGBA32, false);
        generatedTexture.filterMode = FilterMode.Point;
        material.SetTexture("_MainTex", generatedTexture);

        colorMap = new Color[totalXPixels * totalYPixels];
        ResetCanvas();

        minXWorld = topLeftCorner.localPosition.x;
        maxXWorld = bottomRightCorner.localPosition.x;

        minYWorld = bottomRightCorner.localPosition.y;
        maxYWorld = topLeftCorner.localPosition.y;

        baseZWorld = topLeftCorner.localPosition.z;
    }

    private void Update()
    {
        if (Input.GetKeyDown(resetKey)) ResetCanvas();
        if (Input.GetKeyDown(exportKey)) ExportCanvasToPNG();

        CalculatePixelFromPotentiometers();
    }

    void CalculatePixelFromPotentiometers()
    {
        bool shouldDraw = penAlwaysDown || Input.GetKey(drawKey);

        float normX_Input = Mathf.Clamp01(sensorReader.normalizedX);
        float normY_Input = Mathf.Clamp01(sensorReader.normalizedY);

        xPixel = Mathf.Clamp(Mathf.RoundToInt(normX_Input * (totalXPixels - 1)), 0, totalXPixels - 1);
        yPixel = Mathf.Clamp(Mathf.RoundToInt(normY_Input * (totalYPixels - 1)), 0, totalYPixels - 1);

        float worldX = Mathf.Lerp(minXWorld, maxXWorld, normX_Input);
        float worldY = Mathf.Lerp(minYWorld, maxYWorld, normY_Input);

        point.localPosition = new Vector3(worldX, worldY, baseZWorld);

        if (shouldDraw)
        {
            ChangePixelsAroundPoint();
        }
        else
        {
            pressedLastFrame = false;
        }
    }

    void ChangePixelsAroundPoint()
    {
        bool moved = (lastX != xPixel || lastY != yPixel);

        // Nothing new to paint and texture is already up to date.
        if (pressedLastFrame && !moved) return;

        if (useInterpolation && pressedLastFrame && moved)
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
        {
            DrawBrush(xPixel, yPixel);
        }

        pressedLastFrame = true;
        lastX = xPixel;
        lastY = yPixel;
        SetTexture();
    }

    void DrawBrush(int xPix, int yPix)
    {
        int brushSizeInt = Mathf.CeilToInt(brushSize);

        int i = xPix - brushSizeInt + 1, j = yPix - brushSizeInt + 1, maxi = xPix + brushSizeInt - 1, maxj = yPix + brushSizeInt - 1;

        if (i < 0) i = 0;
        if (j < 0) j = 0;
        if (maxi >= totalXPixels) maxi = totalXPixels - 1;
        if (maxj >= totalYPixels) maxj = totalYPixels - 1;

        for (int x = i; x <= maxi; x++)
        {
            for (int y = j; y <= maxj; y++)
            {
                if ((x - xPix) * (x - xPix) + (y - yPix) * (y - yPix) <= brushSize * brushSize)
                {
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

    public void ResetCanvas()
    {
        for (int i = 0; i < colorMap.Length; i++)
            colorMap[i] = canvasColor;
        SetTexture();
        Debug.Log("Canvas has been reset.");
    }

    public void ExportCanvasToPNG()
    {
        if (generatedTexture == null)
        {
            Debug.LogError("Texture not initialized. Cannot export.");
            return;
        }

        byte[] bytes = generatedTexture.EncodeToPNG();

        string fileName = $"Drawing_{System.DateTime.Now:yyyyMMdd_HHmmss}.png";
        string filePath = Path.Combine(Application.persistentDataPath, fileName);

        try
        {
            File.WriteAllBytes(filePath, bytes);
            Debug.Log($"Successfully exported canvas to: {filePath}");
        }
        catch (System.Exception e)
        {
            Debug.LogError($"Failed to export canvas: {e.Message}");
        }
    }
}