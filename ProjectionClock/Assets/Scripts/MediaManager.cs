using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Video;

/// <summary>
/// Plays numbered videos and stills from StreamingAssets/Media on the clock face.
/// Show flow is still (png) -> animation (mp4) -> still hold (png) -> ...
/// Videos play once and hold their last frame; when one finishes and the next item
/// is a still, that still comes up automatically. The face never goes black.
/// Uses two layers (A/B): the next item loads and prepares on the hidden layer,
/// then cuts or crossfades in, so there is no black flash or frozen frame.
/// Files are listed in filename order, with numbers compared by value (2 before 10).
/// Play/Cue take the number a filename starts with (12_storm.mp4 = 12), while
/// Next/Previous step through the list.
/// </summary>
public class MediaManager : MonoBehaviour
{
    public class MediaItem
    {
        public string Path;
        public string Name;
        public bool IsVideo;
        /// <summary>The number the filename starts with (12_storm.mp4 = 12), or -1 if it has none.</summary>
        public int Number;
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

    [Header("Debug")]
    [SerializeField] bool debugOverlay = true;

    public event Action OnListChanged;
    public event Action OnMediaChanged;

    readonly List<MediaItem> items = new List<MediaItem>();
    Layer front, back; // front = on screen, back = hidden (loading or preloaded)
    int currentIndex = -1; // the file playing, or loading to play; moves as soon as Play is called
    int cuedIndex = -1;    // what Next plays: the file after currentIndex unless media/cue picked one
    bool manualCue;        // true when media/cue set cuedIndex, so rescans and failed loads keep it

    Coroutine transition, preload;
    bool transitioning;    // tracked separately: a coroutine can finish before StartCoroutine returns
    Layer incoming;        // layer the running transition is loading or fading in
    bool incomingVisible;

    GUIStyle overlayStyle;

    public IReadOnlyList<MediaItem> Items => items;
    /// <summary>Filename number of the item on the face, -1 if nothing is on it or the file has no number.</summary>
    public int CurrentNumber => currentIndex >= 0 ? items[currentIndex].Number : -1;
    /// <summary>Filename number of what Next will play, -1 if nothing is cued or the file has no number.</summary>
    public int CuedNumber => cuedIndex >= 0 ? items[cuedIndex].Number : -1;
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
        if (playFirstOnStart) PlayFirst();
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
        // Remember the queue by path; indexes change when files are added or renamed.
        string currentPath = PathAt(currentIndex);
        string cuedPath = manualCue ? PathAt(cuedIndex) : null;
        items.Clear();

        string folder = Path.Combine(Application.streamingAssetsPath, mediaFolder);
        if (!Directory.Exists(folder))
        {
            Debug.LogWarning($"MediaManager: media folder not found: {folder}");
        }
        else
        {
            string[] files = Directory.GetFiles(folder);
            Array.Sort(files, (a, b) => NaturalCompare(Path.GetFileName(a), Path.GetFileName(b)));
            foreach (string file in files)
            {
                string ext = Path.GetExtension(file).ToLowerInvariant();
                bool isVideo = Array.IndexOf(VideoExtensions, ext) >= 0;
                if (!isVideo && Array.IndexOf(ImageExtensions, ext) < 0) continue; // skips .meta and anything else
                string name = Path.GetFileName(file);
                items.Add(new MediaItem { Path = file, Name = name, IsVideo = isVideo, Number = LeadingNumber(name) });
            }
            Debug.Log($"MediaManager: found {items.Count} item(s) in {folder}");
            WarnAboutNumbers();
        }

        OnListChanged?.Invoke();

        // Put the queue back on the same files. If the current one is gone, fall back to
        // what's on screen; if a cued one is gone, cue the file after the current one.
        currentIndex = IndexOfPath(currentPath);
        if (currentIndex < 0) currentIndex = IndexOfPath(front.LoadedPath);
        int cued = IndexOfPath(cuedPath);
        manualCue = cued >= 0;
        cuedIndex = manualCue ? cued : IndexAfter(currentIndex);
        QueueChanged("rescan");
        PreloadCued();
    }

    /// <summary>Cuts or crossfades to item number (1-based).</summary>
    public bool Play(int number)
    {
        int index = IndexOfNumber(number);
        if (index < 0)
        {
            Debug.LogWarning($"MediaManager: no media file starts with {number}.");
            return false;
        }
        return PlayAt(index);
    }

    /// <summary>Plays the first file in the list, whatever its number.</summary>
    public bool PlayFirst() => items.Count > 0 && PlayAt(0);

    bool PlayAt(int index)
    {
        StopPreload();
        if (transitioning)
        {
            StopCoroutine(transition);
            FinishInterruptedTransition();
        }

        // Move the queue to this spot now, not when the file reaches the screen (a video
        // can take a second to load), so Next pressed in the meantime continues from here.
        currentIndex = index;
        cuedIndex = IndexAfter(index);
        manualCue = false;
        QueueChanged($"play {items[index].Name}");

        transitioning = true;
        transition = StartCoroutine(TransitionTo(index));
        return true;
    }

    /// <summary>Plays the cued item (normally the next file in the list).</summary>
    public void Next()
    {
        if (cuedIndex >= 0) PlayAt(cuedIndex);
    }

    /// <summary>Plays the file before the current one in the list.</summary>
    public void Previous()
    {
        int index = currentIndex - 1;
        if (index < 0 && wrapAround) index = items.Count - 1;
        if (index >= 0) PlayAt(index);
    }

    /// <summary>Sets what Next will play, by the number its filename starts with, and preloads it.</summary>
    public bool Cue(int number)
    {
        int index = IndexOfNumber(number);
        if (index < 0)
        {
            Debug.LogWarning($"MediaManager: no media file starts with {number}, nothing cued.");
            return false;
        }
        cuedIndex = index;
        manualCue = true;
        QueueChanged($"cue {items[index].Name}");
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
            // Load failed; keep showing whatever was on screen and put the queue back there.
            incoming = null;
            transitioning = false;
            SyncQueueToScreen($"{item.Name} failed to load");
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
        // The queue already moved when Play was called. This only catches the case where the
        // file on screen somehow isn't the queue's current one, and lines them back up.
        if (IndexOfPath(front.LoadedPath) != currentIndex) SyncQueueToScreen("now on screen");
    }

    /// <summary>
    /// Points the queue at the file actually on the face. Keeps a media/cue choice;
    /// otherwise cues the file after it.
    /// </summary>
    void SyncQueueToScreen(string reason)
    {
        currentIndex = IndexOfPath(front.LoadedPath);
        if (!manualCue) cuedIndex = IndexAfter(currentIndex);
        QueueChanged(reason);
    }

    /// <summary>Tells listeners the queue moved, and logs why so a surprising "next" can be traced.</summary>
    void QueueChanged(string reason)
    {
        Debug.Log($"MediaManager: queue ({reason}): now {NameAt(currentIndex)}, next {NameAt(cuedIndex)}{(manualCue ? " (cued)" : "")}");
        OnMediaChanged?.Invoke();
    }

    string PathAt(int index) => index >= 0 && index < items.Count ? items[index].Path : null;
    string NameAt(int index) => index >= 0 && index < items.Count ? items[index].Name : "none";
    int IndexOfPath(string path) => path == null ? -1 : items.FindIndex(i => i.Path == path);

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

    /// <summary>Index of the first file (in list order) whose name starts with this number, or -1.</summary>
    int IndexOfNumber(int number) => number < 0 ? -1 : items.FindIndex(i => i.Number == number);

    /// <summary>The number a filename starts with ("12_storm.mp4" = 12, "007.png" = 7), or -1 if none.</summary>
    static int LeadingNumber(string name)
    {
        int length = 0;
        while (length < name.Length && char.IsDigit(name[length])) length++;
        return length > 0 && int.TryParse(name.Substring(0, length), out int number) ? number : -1;
    }

    /// <summary>Points out files that media/play N can't reach unambiguously.</summary>
    void WarnAboutNumbers()
    {
        var seen = new Dictionary<int, string>();
        foreach (MediaItem item in items)
        {
            if (item.Number < 0)
                Debug.LogWarning($"MediaManager: {item.Name} doesn't start with a number, so only Next/Prev can reach it.");
            else if (seen.TryGetValue(item.Number, out string first))
                Debug.LogWarning($"MediaManager: {first} and {item.Name} both start with {item.Number}; media/play {item.Number} plays {first}.");
            else
                seen[item.Number] = item.Name;
        }
    }

    /// <summary>
    /// Filename order where runs of digits compare by value, so 2_storm sorts before 10_storm
    /// (plain alphabetical order would put 10 right after 1). Otherwise case-insensitive.
    /// </summary>
    static int NaturalCompare(string a, string b)
    {
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
            {
                int startA = i, startB = j;
                while (i < a.Length && char.IsDigit(a[i])) i++;
                while (j < b.Length && char.IsDigit(b[j])) j++;

                // Compare as numbers without parsing, so any length works: drop leading
                // zeros, then the longer run is bigger, and equal lengths compare digit by digit.
                string numberA = a.Substring(startA, i - startA).TrimStart('0');
                string numberB = b.Substring(startB, j - startB).TrimStart('0');
                if (numberA.Length != numberB.Length) return numberA.Length.CompareTo(numberB.Length);
                int digits = string.CompareOrdinal(numberA, numberB);
                if (digits != 0) return digits;
            }
            else
            {
                int letters = char.ToLowerInvariant(a[i]).CompareTo(char.ToLowerInvariant(b[j]));
                if (letters != 0) return letters;
                i++;
                j++;
            }
        }

        int remaining = (a.Length - i).CompareTo(b.Length - j);
        return remaining != 0 ? remaining : string.CompareOrdinal(a, b); // tie-break, e.g. "01" vs "1"
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

        string current = NameAt(currentIndex);
        string cued = NameAt(cuedIndex) + (manualCue ? " (cued)" : "");
        string state = "";
        if (IsLoading) state = "  (loading...)";
        else if (currentIndex >= 0 && items[currentIndex].IsVideo)
            state = IsVideoPlaying ? "  (playing)" : "  (holding last frame)";

        var content = new GUIContent(
            $"Media ({items.Count}):  now {current}{state}   next {cued}");
        Vector2 size = overlayStyle.CalcSize(content);
        GUI.Label(new Rect(10, 60, size.x, size.y), content, overlayStyle);
    }
}
