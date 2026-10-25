using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.UI;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>
/// Sends the show canvas to the projector (Display 2) and a preview to the laptop (Display 1),
/// and runs calibration mode for lining the mapped regions up with the physical prop.
/// The show canvas renders into one render texture and both displays show that texture,
/// so the preview is exactly what the projector gets. Calibration is saved as JSON in
/// Application.persistentDataPath and loaded at startup, so you calibrate once per venue.
/// </summary>
public class OutputManager : MonoBehaviour
{
    [Serializable]
    class RegionData
    {
        public string name;
        public Vector2 position;
        public Vector2 scale;
        public float rotation;
    }

    [Serializable]
    class CalibrationFile
    {
        public List<RegionData> regions = new List<RegionData>();
    }

    struct RegionDefault
    {
        public Vector2 Position;
        public Vector3 Scale;
        public Quaternion Rotation;
    }

    [Header("Show canvas (switched to render for the projector at runtime)")]
    [SerializeField] Canvas outputCanvas;

    [Header("Projector")]
    [Tooltip("Used when there's no second display (e.g. in the editor). With a projector attached, its native resolution is used.")]
    [SerializeField] Vector2Int fallbackResolution = new Vector2Int(1920, 1080);
    [Tooltip("Show what the projector gets on the laptop screen.")]
    [SerializeField] bool showPreview = true;

    [Header("Calibration")]
    [Tooltip("Mapped regions, picked with 1-9 in calibration mode. Each moves, scales and rotates on its own.")]
    [SerializeField] List<RectTransform> regions = new List<RectTransform>();
    [SerializeField] string calibrationFileName = "calibration.json";
    [Tooltip("Canvas units per second.")]
    [SerializeField] float moveSpeed = 200f;
    [Tooltip("Fraction of size per second.")]
    [SerializeField] float scaleSpeed = 0.25f;
    [Tooltip("Degrees per second.")]
    [SerializeField] float rotateSpeed = 20f;
    [Tooltip("Speed multiplier while Shift is held.")]
    [SerializeField] float fineFactor = 0.1f;

    public event Action<bool> OnCalibrationChanged;

    /// <summary>Set by ShowController while its command box has focus, so typing doesn't move regions.</summary>
    public bool KeyboardBlocked { get; set; }
    public bool IsCalibrating => calibrating;
    /// <summary>Exactly what the projector shows. The operator UI uses this for its preview.</summary>
    public RenderTexture OutputTexture => outputTexture;

    RenderTexture outputTexture;
    Camera outputCamera;
    readonly List<RegionDefault> defaults = new List<RegionDefault>();
    readonly List<RawImage> patterns = new List<RawImage>();
    Texture2D outlineTexture, fillTexture;
    bool calibrating;
    bool solidFill;
    int selected;
    GUIStyle overlayStyle;

    string CalibrationPath => Path.Combine(Application.persistentDataPath, calibrationFileName);

    void Awake()
    {
        if (outputCanvas == null)
        {
            Debug.LogError("OutputManager: assign the show Canvas in the Inspector.");
            enabled = false;
            return;
        }

        regions.RemoveAll(r => r == null);
        foreach (RectTransform region in regions)
        {
            defaults.Add(new RegionDefault
            {
                Position = region.anchoredPosition,
                Scale = region.localScale,
                Rotation = region.localRotation
            });
        }

        SetUpOutput();
        CreateTestPatterns();
        LoadCalibration();
    }

    void Update()
    {
#if ENABLE_INPUT_SYSTEM
        if (calibrating && !KeyboardBlocked) HandleCalibrationKeys();
#endif
    }

    void OnApplicationQuit()
    {
        // Covers Alt+F4 or closing the window mid-calibration.
        if (calibrating) SaveCalibration();
    }

    void OnDestroy()
    {
        if (outputTexture != null)
        {
            outputTexture.Release();
            Destroy(outputTexture);
        }
        if (outlineTexture != null) Destroy(outlineTexture);
        if (fillTexture != null) Destroy(fillTexture);
    }

    // ---- Control API (ShowController calls these) ----

    public void SetCalibrating(bool on)
    {
        if (calibrating == on) return;
        calibrating = on;
        if (!on) SaveCalibration();
        RefreshPatterns();
        OnCalibrationChanged?.Invoke(on);
    }

    public void ToggleCalibrating() => SetCalibrating(!calibrating);

    public void SaveCalibration()
    {
        var file = new CalibrationFile();
        foreach (RectTransform region in regions)
        {
            file.regions.Add(new RegionData
            {
                name = region.name,
                position = region.anchoredPosition,
                scale = region.localScale,
                rotation = Mathf.DeltaAngle(0f, region.localEulerAngles.z)
            });
        }

        try
        {
            File.WriteAllText(CalibrationPath, JsonUtility.ToJson(file, true));
            Debug.Log($"OutputManager: calibration saved to {CalibrationPath}");
        }
        catch (Exception e)
        {
            Debug.LogError($"OutputManager: couldn't save calibration: {e.Message}");
        }
    }

    // ---- Output ----

    void SetUpOutput()
    {
        bool hasProjector = Display.displays.Length > 1;
        Vector2Int size = fallbackResolution;
        if (hasProjector)
        {
            Display projector = Display.displays[1];
            projector.Activate();
            size = new Vector2Int(projector.systemWidth, projector.systemHeight);
        }

        outputTexture = new RenderTexture(size.x, size.y, 24) { name = "Projector Output" };
        outputTexture.Create();

        // Far from anything else in the scene so no other camera sees the show canvas.
        var cameraObject = new GameObject("Output Camera");
        cameraObject.transform.SetParent(transform, false);
        cameraObject.transform.position = new Vector3(10000f, 10000f, 0f);
        outputCamera = cameraObject.AddComponent<Camera>();
        outputCamera.clearFlags = CameraClearFlags.SolidColor;
        outputCamera.backgroundColor = Color.black;
        outputCamera.orthographic = true;
        outputCamera.cullingMask = 1 << outputCanvas.gameObject.layer;
        outputCamera.targetTexture = outputTexture;

        outputCanvas.renderMode = RenderMode.ScreenSpaceCamera;
        outputCanvas.worldCamera = outputCamera;
        outputCanvas.planeDistance = 10f;

        // Always made, so the editor's "Display 2" Game view shows it too. With no
        // second display in a build it simply isn't shown anywhere.
        CreateScreen("Projector Screen", 1);
        if (showPreview || !hasProjector) CreateScreen("Output Preview", 0);

        Debug.Log(hasProjector
            ? $"OutputManager: projector output on Display 2 at {size.x}x{size.y}."
            : $"OutputManager: no second display found, showing output on this screen at {size.x}x{size.y}.");
    }

    /// <summary>
    /// A full-screen canvas on one display that shows the output texture. It always keeps
    /// the texture's aspect ratio (black bars if the screen differs), because a stretched
    /// output would throw the mapping off.
    /// </summary>
    void CreateScreen(string name, int displayIndex)
    {
        var screenObject = new GameObject(name, typeof(RectTransform));
        screenObject.transform.SetParent(transform, false);

        if (displayIndex != 0)
        {
            // Display 1 already has the Main Camera. Other displays need a camera of their
            // own that clears to black, or they show "No cameras rendering" / stale pixels.
            var clearCamera = new GameObject($"{name} Camera").AddComponent<Camera>();
            clearCamera.transform.SetParent(screenObject.transform, false);
            clearCamera.targetDisplay = displayIndex;
            clearCamera.clearFlags = CameraClearFlags.SolidColor;
            clearCamera.backgroundColor = Color.black;
            clearCamera.cullingMask = 0;
            clearCamera.orthographic = true;
        }

        var canvas = screenObject.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.targetDisplay = displayIndex;
        canvas.sortingOrder = -100; // behind the operator UI

        var imageObject = new GameObject("Output", typeof(RectTransform));
        imageObject.transform.SetParent(screenObject.transform, false);
        var image = imageObject.AddComponent<RawImage>();
        image.texture = outputTexture;
        image.raycastTarget = false;
        Stretch(image.rectTransform);

        var fitter = imageObject.AddComponent<AspectRatioFitter>();
        fitter.aspectMode = AspectRatioFitter.AspectMode.FitInParent;
        fitter.aspectRatio = (float)outputTexture.width / outputTexture.height;
    }

    // ---- Calibration ----

#if ENABLE_INPUT_SYSTEM
    void HandleCalibrationKeys()
    {
        Keyboard kb = Keyboard.current;
        if (kb == null) return;

        if (kb.escapeKey.wasPressedThisFrame)
        {
            SetCalibrating(false);
            return;
        }
        for (int i = 0; i < regions.Count && i < 9; i++)
        {
            if (kb[Key.Digit1 + i].wasPressedThisFrame)
            {
                selected = i;
                RefreshPatterns();
            }
        }
        if (kb.fKey.wasPressedThisFrame)
        {
            solidFill = !solidFill;
            RefreshPatterns();
        }
        if (kb.backspaceKey.wasPressedThisFrame) ResetRegion(selected);
        if (regions.Count == 0) return;

        RectTransform region = regions[selected];
        float dt = Time.unscaledDeltaTime * (kb.shiftKey.isPressed ? fineFactor : 1f);
        var arrows = new Vector2(
            (kb.rightArrowKey.isPressed ? 1f : 0f) - (kb.leftArrowKey.isPressed ? 1f : 0f),
            (kb.upArrowKey.isPressed ? 1f : 0f) - (kb.downArrowKey.isPressed ? 1f : 0f));

        Vector3 scale = region.localScale;
        if (kb.ctrlKey.isPressed)
        {
            // Ctrl + arrows stretches width / height, for an oval or off-axis projector.
            scale.x *= 1f + arrows.x * scaleSpeed * dt;
            scale.y *= 1f + arrows.y * scaleSpeed * dt;
        }
        else
        {
            region.anchoredPosition += arrows * (moveSpeed * dt);
        }

        bool grow = kb.equalsKey.isPressed || kb.numpadPlusKey.isPressed;
        bool shrink = kb.minusKey.isPressed || kb.numpadMinusKey.isPressed;
        float zoom = (grow ? 1f : 0f) - (shrink ? 1f : 0f);
        scale.x *= 1f + zoom * scaleSpeed * dt;
        scale.y *= 1f + zoom * scaleSpeed * dt;
        region.localScale = new Vector3(scale.x, scale.y, 1f);

        float spin = (kb.eKey.isPressed ? 1f : 0f) - (kb.qKey.isPressed ? 1f : 0f);
        if (spin != 0f) region.localRotation *= Quaternion.Euler(0f, 0f, -spin * rotateSpeed * dt); // E = clockwise
    }
#endif

    void ResetRegion(int index)
    {
        if (index < 0 || index >= regions.Count) return;
        regions[index].anchoredPosition = defaults[index].Position;
        regions[index].localScale = defaults[index].Scale;
        regions[index].localRotation = defaults[index].Rotation;
    }

    void LoadCalibration()
    {
        if (!File.Exists(CalibrationPath))
        {
            Debug.Log($"OutputManager: no calibration saved yet ({CalibrationPath}), using the scene layout.");
            return;
        }

        CalibrationFile file;
        try
        {
            file = JsonUtility.FromJson<CalibrationFile>(File.ReadAllText(CalibrationPath));
        }
        catch (Exception e)
        {
            Debug.LogError($"OutputManager: couldn't read calibration, using the scene layout: {e.Message}");
            return;
        }

        foreach (RegionData data in file.regions)
        {
            RectTransform region = regions.Find(r => r.name == data.name);
            if (region == null) continue;
            region.anchoredPosition = data.position;
            region.localScale = new Vector3(data.scale.x, data.scale.y, 1f);
            region.localRotation = Quaternion.Euler(0f, 0f, data.rotation);
        }
        Debug.Log($"OutputManager: calibration loaded from {CalibrationPath}");
    }

    void CreateTestPatterns()
    {
        outlineTexture = MakePattern(1024, false);
        fillTexture = MakePattern(1024, true);

        foreach (RectTransform region in regions)
        {
            var patternObject = new GameObject("Calibration Pattern", typeof(RectTransform));
            patternObject.transform.SetParent(region, false);
            var image = patternObject.AddComponent<RawImage>();
            image.raycastTarget = false;
            Stretch(image.rectTransform);
            patternObject.SetActive(false);
            patterns.Add(image);
        }
        RefreshPatterns();
    }

    void RefreshPatterns()
    {
        for (int i = 0; i < patterns.Count; i++)
        {
            RawImage pattern = patterns[i];
            pattern.gameObject.SetActive(calibrating);
            pattern.texture = solidFill ? fillTexture : outlineTexture;
            pattern.color = i == selected ? Color.white : new Color(1f, 1f, 1f, 0.35f); // dim the unselected ones
            pattern.transform.SetAsLastSibling(); // above the media and hands
        }
    }

    /// <summary>White test pattern: edge ring, half ring, crosshair and hour ticks, or a solid disk.</summary>
    static Texture2D MakePattern(int size, bool solid)
    {
        var pixels = new Color32[size * size];
        float half = size / 2f;
        float line = size / 400f;
        float step = Mathf.PI / 6f;

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dx = x + 0.5f - half;
                float dy = y + 0.5f - half;
                float r = Mathf.Sqrt(dx * dx + dy * dy);
                float alpha;

                if (solid)
                {
                    alpha = Coverage(half - 1f - r);
                }
                else
                {
                    float edge = Coverage(line - Mathf.Abs(r - (half - line - 1f)));
                    float middle = Coverage(line * 0.5f - Mathf.Abs(r - half * 0.5f));
                    float cross = r < half ? Mathf.Max(Coverage(line * 0.5f - Mathf.Abs(dx)), Coverage(line * 0.5f - Mathf.Abs(dy))) : 0f;
                    float angle = Mathf.Atan2(dy, dx);
                    float offTick = Mathf.Abs(r * Mathf.Sin(angle - Mathf.Round(angle / step) * step));
                    float tick = r > half * 0.85f && r < half ? Coverage(line - offTick) : 0f;
                    alpha = Mathf.Max(Mathf.Max(edge, middle), Mathf.Max(cross, tick));
                }

                pixels[y * size + x] = new Color32(255, 255, 255, (byte)(alpha * 255f));
            }
        }

        var texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
        {
            name = solid ? "Calibration Fill" : "Calibration Outline",
            wrapMode = TextureWrapMode.Clamp
        };
        texture.SetPixels32(pixels);
        texture.Apply();
        return texture;
    }

    /// <summary>Anti-aliased coverage from a signed distance in pixels (positive = inside).</summary>
    static float Coverage(float distance) => Mathf.Clamp01(distance + 0.5f);

    static void Stretch(RectTransform rect)
    {
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = rect.offsetMax = Vector2.zero;
    }

    // ---- Debug overlay ----

    void OnGUI()
    {
        if (!calibrating || regions.Count == 0) return;
        if (overlayStyle == null)
        {
            // Solid dark box so the text reads over any background.
            var background = new Texture2D(1, 1);
            background.SetPixel(0, 0, new Color(0f, 0f, 0f, 0.8f));
            background.Apply();
            overlayStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 22,
                padding = new RectOffset(12, 12, 6, 6),
                normal = { textColor = Color.yellow, background = background }
            };
        }

        RectTransform region = regions[selected];
        Vector2 p = region.anchoredPosition;
        Vector3 s = region.localScale;
        float rotation = Mathf.DeltaAngle(0f, region.localEulerAngles.z);
        var content = new GUIContent(
            $"CALIBRATING  region {selected + 1}/{regions.Count}: {region.name}   " +
            $"pos ({p.x:0.0}, {p.y:0.0})   scale ({s.x:0.000} x {s.y:0.000})   rotation {rotation:0.00}°\n" +
            "[1-9] region   [arrows] move   [Ctrl+arrows] stretch   [+/-] size   [Q/E] rotate   [Shift] fine\n" +
            "[F] white fill / outline   [Backspace] reset region   [Esc] or [C] save and exit");
        Vector2 size = overlayStyle.CalcSize(content);
        GUI.Label(new Rect(10, Screen.height - size.y - 10, size.x, size.y), content, overlayStyle);
    }
}
