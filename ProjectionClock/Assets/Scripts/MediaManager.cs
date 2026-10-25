using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Video;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>
/// Plays numbered videos and stills from StreamingAssets/Media on the clock face.
/// Show flow is still (png) -> animation (mp4) -> still hold (png) -> ...
/// Videos play once and hold their last frame; when one finishes and the next item
/// is a still, that still comes up automatically. The face never goes black.
/// Uses two layers (A/B): the next item loads and prepares on the hidden layer,
/// then cuts or crossfades in, so there is no black flash or frozen frame.
/// Item numbers are 1-based and follow filename order (01_intro.png = 1).
/// </summary>
public class MediaManager : MonoBehaviour
{
    public class MediaItem
    {
        public string Path;
        public string Name;
        public bool IsVideo;
    }

    class Layer
    {
        public string Label;
        public RawImage Image;
        public VideoPlayer Player;
        public RenderTexture Target;
        public Texture2D Still;
        public string LoadedPath;
        public bool Failed;
    }

    static readonly string[] VideoExtensions = { ".mp4", ".m4v", ".mov", ".webm" };
    static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg" };

    [Header("Face layers (stacked RawImages inside the face mask)")]
    [SerializeField] RawImage layerA;
    [SerializeField] RawImage layerB;

    [Header("Media")]
    [Tooltip("Folder inside StreamingAssets.")]
    [SerializeField] string mediaFolder = "Media";
    [SerializeField] int renderTextureSize = 2048;
    [SerializeField] bool playVideoAudio = false;
    [Tooltip("Show item 1 at launch so the face is never empty.")]
    [SerializeField] bool playFirstOnStart = true;
    [Tooltip("When a video finishes and the next item is a still, show that still automatically.")]
    [SerializeField] bool autoAdvanceToStill = true;
    [Tooltip("After the last item, Next goes back to item 1.")]
    [SerializeField] bool wrapAround = false;

    [Header("Transitions")]
    [Tooltip("0 = hard cut, best when each still matches the frame its video starts or ends on.")]
    [SerializeField, Range(0f, 5f)] float crossfadeSeconds = 0f;
    [SerializeField] float prepareTimeout = 10f;

    [Header("Debug (ShowController replaces this in step 4)")]
    [SerializeField] bool debugKeys = true;
    [SerializeField] bool debugOverlay = true;

    public event Action OnListChanged;
    public event Action OnMediaChanged;

    readonly List<MediaItem> items = new List<MediaItem>();
    Layer front, back; // front = on screen, back = hidden (loading or preloaded)
    int currentIndex = -1;
    int cuedIndex = -1;

    Coroutine transition, preload;
    bool transitioning;    // tracked separately: a coroutine can finish before StartCoroutine returns
    Layer incoming;        // layer the running transition is loading or fading in
    bool incomingVisible;

    GUIStyle overlayStyle;

    public IReadOnlyList<MediaItem> Items => items;
    /// <summary>1-based number of the item on the face, 0 if none.</summary>
    public int CurrentNumber => currentIndex + 1;
    /// <summary>1-based number of what Next will play, 0 if nothing.</summary>
    public int CuedNumber => cuedIndex + 1;
    public bool IsLoading => transitioning && !incomingVisible;
    /// <summary>True while a video on the face is still playing (false once it holds its last frame).</summary>
    public bool IsVideoPlaying => front != null && front.Player.isPlaying;

    void Awake()
    {
        if (layerA == null || layerB == null)
        {
            Debug.LogError("MediaManager: assign Layer A and Layer B in the Inspector.");
            enabled = false;
            return;
        }

        front = CreateLayer(layerA, "A");
        back = CreateLayer(layerB, "B");
        Rescan();
    }

    void Start()
    {
        if (playFirstOnStart && items.Count > 0) Play(1);
    }

    void Update()
    {
        if (debugKeys) HandleDebugKeys();
    }

    void OnDestroy()
    {
        foreach (Layer layer in new[] { front, back })
        {
            if (layer == null) continue;
            if (layer.Still != null) Destroy(layer.Still);
            layer.Target.Release();
            Destroy(layer.Target);
        }
    }

    // ---- Control API (ShowController calls these from step 4 on) ----

    /// <summary>Re-reads the media folder. Safe to call mid-show after swapping files.</summary>
    public void Rescan()
    {
        StopPreload();
        items.Clear();

        string folder = Path.Combine(Application.streamingAssetsPath, mediaFolder);
        if (!Directory.Exists(folder))
        {
            Debug.LogWarning($"MediaManager: media folder not found: {folder}");
        }
        else
        {
            string[] files = Directory.GetFiles(folder);
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            foreach (string file in files)
            {
                string ext = Path.GetExtension(file).ToLowerInvariant();
                bool isVideo = Array.IndexOf(VideoExtensions, ext) >= 0;
                if (!isVideo && Array.IndexOf(ImageExtensions, ext) < 0) continue; // skips .meta and anything else
                items.Add(new MediaItem { Path = file, Name = Path.GetFileName(file), IsVideo = isVideo });
            }
            Debug.Log($"MediaManager: found {items.Count} item(s) in {folder}");
        }

        // Files may have been added or renamed, so find what's on screen again by path.
        currentIndex = front.LoadedPath != null ? items.FindIndex(i => i.Path == front.LoadedPath) : -1;
        cuedIndex = IndexAfter(currentIndex);
        OnListChanged?.Invoke();
        OnMediaChanged?.Invoke();
        PreloadCued();
    }

    /// <summary>Cuts or crossfades to item number (1-based).</summary>
    public bool Play(int number)
    {
        int index = number - 1;
        if (index < 0 || index >= items.Count)
        {
            Debug.LogWarning($"MediaManager: there is no media item {number} (have {items.Count}).");
            return false;
        }

        StopPreload();
        if (transitioning)
        {
            StopCoroutine(transition);
            FinishInterruptedTransition();
        }
        transitioning = true;
        transition = StartCoroutine(TransitionTo(index));
        return true;
    }

    /// <summary>Plays the cued item (normally the one after the current item).</summary>
    public void Next()
    {
        if (cuedIndex >= 0) Play(cuedIndex + 1);
    }

    public void Previous()
    {
        int index = currentIndex - 1;
        if (index < 0 && wrapAround) index = items.Count - 1;
        if (index >= 0) Play(index + 1);
    }

    /// <summary>Sets what Next will play and preloads it.</summary>
    public bool Cue(int number)
    {
        int index = number - 1;
        if (index < 0 || index >= items.Count)
        {
            Debug.LogWarning($"MediaManager: there is no media item {number} to cue.");
            return false;
        }
        cuedIndex = index;
        OnMediaChanged?.Invoke();
        PreloadCued();
        return true;
    }

    // ---- Transitions ----

    /// <summary>A video on the face reached its end and is holding its last frame.</summary>
    void OnVideoFinished(Layer layer)
    {
        if (layer != front || transitioning || !autoAdvanceToStill) return;
        if (cuedIndex >= 0 && !items[cuedIndex].IsVideo) Next();
        // Otherwise keep holding the last frame until the operator cues something.
    }

    IEnumerator TransitionTo(int index)
    {
        MediaItem item = items[index];
        incoming = back;
        incomingVisible = false;

        yield return Load(back, item);
        if (back.LoadedPath != item.Path)
        {
            // Load failed; keep showing whatever was on screen.
            incoming = null;
            transitioning = false;
            PreloadCued();
            yield break;
        }

        if (item.IsVideo)
        {
            back.Player.Play();
            // Wait for a real frame so the fade never starts on an empty texture.
            for (float t = 0f; back.Player.frame < 1 && t < 1f; t += Time.unscaledDeltaTime)
                yield return null;
        }

        back.Image.texture = item.IsVideo ? back.Target : back.Still;
        back.Image.uvRect = item.IsVideo ? new Rect(0f, 0f, 1f, 1f) : CoverUv(back.Still);
        back.Image.transform.SetAsLastSibling(); // draw above the outgoing layer
        incomingVisible = true;

        for (float t = 0f; t < crossfadeSeconds; t += Time.unscaledDeltaTime)
        {
            SetAlpha(back.Image, t / crossfadeSeconds);
            yield return null;
        }

        CompleteSwap();
        transitioning = false;
        PreloadCued();
    }

    /// <summary>A new Play arrived mid-transition: jump an in-progress fade to its end, or drop a half-loaded item.</summary>
    void FinishInterruptedTransition()
    {
        transitioning = false;
        if (incoming == null) return;
        if (incomingVisible) CompleteSwap();
        else StopLayer(incoming);
        incoming = null;
    }

    void CompleteSwap()
    {
        SetAlpha(incoming.Image, 1f);
        Layer old = front;
        front = incoming;
        back = old;
        StopLayer(old);

        incoming = null;
        // By path, not index, in case a rescan reordered the list mid-transition.
        currentIndex = items.FindIndex(i => i.Path == front.LoadedPath);
        cuedIndex = IndexAfter(currentIndex);
        OnMediaChanged?.Invoke();
    }

    /// <summary>Loads an item into a hidden layer. On failure the layer's LoadedPath is left null.</summary>
    IEnumerator Load(Layer layer, MediaItem item)
    {
        if (layer.LoadedPath != item.Path)
        {
            StopLayer(layer);
            layer.LoadedPath = item.Path;

            if (item.IsVideo)
            {
                layer.Player.url = item.Path;
                layer.Player.Prepare();
            }
            else
            {
                layer.Still = LoadStill(item.Path);
                if (layer.Still == null) StopLayer(layer);
                yield break;
            }
        }
        else if (!item.IsVideo)
        {
            yield break; // still already preloaded
        }

        // Covers both a fresh Prepare and a preload that is still running.
        for (float t = 0f; !layer.Player.isPrepared; t += Time.unscaledDeltaTime)
        {
            if (layer.Failed || t > prepareTimeout)
            {
                Debug.LogError($"MediaManager: couldn't prepare {item.Name}" + (layer.Failed ? "" : " (timed out)"));
                StopLayer(layer);
                yield break;
            }
            yield return null;
        }
    }

    void PreloadCued()
    {
        if (transitioning || cuedIndex < 0) return;
        StopPreload();
        preload = StartCoroutine(Load(back, items[cuedIndex]));
    }

    void StopPreload()
    {
        if (preload == null) return;
        StopCoroutine(preload);
        preload = null;
    }

    // ---- Layers ----

    Layer CreateLayer(RawImage image, string label)
    {
        // Each layer always covers the whole face mask, whatever it was set to in the scene.
        RectTransform rect = image.rectTransform;
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.offsetMin = rect.offsetMax = Vector2.zero;
        rect.localScale = Vector3.one;
        rect.localRotation = Quaternion.identity;

        var target = new RenderTexture(renderTextureSize, renderTextureSize, 0) { name = $"Face RT {label}" };
        target.Create();

        var go = new GameObject($"VideoPlayer {label}");
        go.transform.SetParent(transform, false);
        var player = go.AddComponent<VideoPlayer>();
        player.playOnAwake = false;
        player.source = VideoSource.Url;
        player.renderMode = VideoRenderMode.RenderTexture;
        player.targetTexture = target;
        player.aspectRatio = VideoAspectRatio.FitOutside; // fill the square face, cropping the sides
        player.isLooping = false; // play once, then the render texture keeps the last frame
        player.skipOnDrop = true;
        player.waitForFirstFrame = true;
        player.audioOutputMode = playVideoAudio ? VideoAudioOutputMode.Direct : VideoAudioOutputMode.None;

        var layer = new Layer { Label = label, Image = image, Player = player, Target = target };
        player.errorReceived += (source, message) =>
        {
            Debug.LogError($"MediaManager: video error on layer {label}: {message}");
            layer.Failed = true;
        };
        player.loopPointReached += source => OnVideoFinished(layer);

        StopLayer(layer);
        return layer;
    }

    void StopLayer(Layer layer)
    {
        layer.Player.Stop();
        if (layer.Still != null)
        {
            Destroy(layer.Still);
            layer.Still = null;
        }
        layer.LoadedPath = null;
        layer.Failed = false;
        layer.Image.texture = null;
        SetAlpha(layer.Image, 0f);
        ClearTexture(layer.Target); // so the next video never flashes this one's last frame
    }

    static Texture2D LoadStill(string path)
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception e)
        {
            Debug.LogError($"MediaManager: couldn't read {path}: {e.Message}");
            return null;
        }

        var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false) { name = Path.GetFileName(path) };
        if (!texture.LoadImage(bytes))
        {
            Debug.LogError($"MediaManager: {path} isn't a valid PNG/JPG.");
            Destroy(texture);
            return null;
        }
        texture.wrapMode = TextureWrapMode.Clamp;
        return texture;
    }

    /// <summary>UV rect that fills a square face with the texture, cropping the long side.</summary>
    static Rect CoverUv(Texture texture)
    {
        float aspect = (float)texture.width / texture.height;
        if (aspect > 1f)
        {
            float w = 1f / aspect;
            return new Rect((1f - w) / 2f, 0f, w, 1f);
        }
        return new Rect(0f, (1f - aspect) / 2f, 1f, aspect);
    }

    static void ClearTexture(RenderTexture texture)
    {
        RenderTexture previous = RenderTexture.active;
        RenderTexture.active = texture;
        GL.Clear(true, true, Color.black);
        RenderTexture.active = previous;
    }

    static void SetAlpha(Graphic graphic, float alpha)
    {
        Color c = graphic.color;
        c.a = Mathf.Clamp01(alpha);
        graphic.color = c;
    }

    int IndexAfter(int index)
    {
        if (items.Count == 0) return -1;
        int next = index + 1;
        if (next >= items.Count) next = wrapAround ? 0 : -1;
        return next;
    }

    // ---- Debug ----

    void HandleDebugKeys()
    {
#if ENABLE_INPUT_SYSTEM
        Keyboard kb = Keyboard.current;
        if (kb == null) return;
        if (kb.nKey.wasPressedThisFrame) Next();
        if (kb.pKey.wasPressedThisFrame) Previous();
        if (kb.f5Key.wasPressedThisFrame) Rescan();
        for (int i = 0; i < 9; i++)
            if (kb[Key.Digit1 + i].wasPressedThisFrame) Play(i + 1);
#else
        if (Input.GetKeyDown(KeyCode.N)) Next();
        if (Input.GetKeyDown(KeyCode.P)) Previous();
        if (Input.GetKeyDown(KeyCode.F5)) Rescan();
        for (int i = 0; i < 9; i++)
            if (Input.GetKeyDown(KeyCode.Alpha1 + i)) Play(i + 1);
#endif
    }

    void OnGUI()
    {
        if (!debugOverlay) return;
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
                normal = { textColor = Color.white, background = background }
            };
        }

        string current = currentIndex >= 0 ? $"{CurrentNumber}. {items[currentIndex].Name}" : "none";
        string cued = cuedIndex >= 0 ? $"{CuedNumber}. {items[cuedIndex].Name}" : "none";
        string state = "";
        if (IsLoading) state = "  (loading...)";
        else if (currentIndex >= 0 && items[currentIndex].IsVideo)
            state = IsVideoPlaying ? "  (playing)" : "  (holding last frame)";

        var content = new GUIContent(
            $"Media ({items.Count}):  now {current}{state}   next {cued}\n" +
            "[N] next   [P] prev   [1-9] play   [F5] rescan folder");
        Vector2 size = overlayStyle.CalcSize(content);
        GUI.Label(new Rect(10, 60, size.x, size.y), content, overlayStyle);
    }
}
