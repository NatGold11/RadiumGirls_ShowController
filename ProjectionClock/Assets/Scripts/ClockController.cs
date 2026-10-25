using System;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>
/// Owns the show clock. Keeps its own time (never reads the system clock) so it
/// can be started, stopped, reset and set to any time on cue.
/// Fires OnTick once per whole second while running.
/// </summary>
public class ClockController : MonoBehaviour
{
    [Header("Hands (rotation centre = RectTransform pivot, art pointing to 12)")]
    [SerializeField] Transform hourHand;
    [SerializeField] Transform minuteHand;
    [SerializeField] Transform secondHand;

    [Header("Reset time (12-hour)")]
    [SerializeField, Range(1, 12)] int resetHour = 12;
    [SerializeField, Range(0, 59)] int resetMinute = 0;
    [SerializeField, Range(0, 59)] int resetSecond = 0;
    [SerializeField] bool runOnStart = false;

    [Header("Tick motion")]
    [Tooltip("Seconds the second hand takes to move one tick.")]
    [SerializeField, Range(0.02f, 0.5f)] float tickDuration = 0.1f;
    [Tooltip("0 = no overshoot. ~1.7 gives a small mechanical bounce.")]
    [SerializeField, Range(0f, 4f)] float overshoot = 1.7f;

    [Header("Debug (ShowController replaces this in step 4)")]
    [SerializeField] bool debugKeys = true;
    [SerializeField] bool debugOverlay = true;
    [SerializeField] string debugJumpTime = "11:59:50";

    public event Action OnTick;
    public event Action OnReset;
    public event Action<bool> OnRunningChanged;

    double elapsedSeconds;
    bool running;

    // Second-hand animation, in whole ticks (1 tick = 6 degrees).
    long fromTick, toTick;
    float tickTimer;

    GUIStyle overlayStyle;

    public double ElapsedSeconds => elapsedSeconds;
    public bool IsRunning => running;
    public float TickDuration => tickDuration;
    public float Overshoot => overshoot;

    /// <summary>Eased progress (0 to 1, briefly overshooting 1) of the most recent tick.</summary>
    public float TickProgress => EaseTick(tickTimer / tickDuration, overshoot);

    long WholeSeconds => (long)Math.Floor(elapsedSeconds);

    void Awake()
    {
        ResetClock();
        if (runOnStart) StartClock();
    }

    void Update()
    {
        if (debugKeys) HandleDebugKeys();

        if (running)
        {
            long before = WholeSeconds;
            elapsedSeconds += Time.deltaTime;
            long after = WholeSeconds;

            if (after != before)
            {
                // After a frame hitch, jump to the previous second and animate only the last step.
                fromTick = after - 1;
                toTick = after;
                tickTimer = 0f;
                for (long i = before; i < after; i++) OnTick?.Invoke();
            }
        }

        tickTimer += Time.deltaTime;
        UpdateHands();
    }

    // ---- Control API (ShowController calls these from step 4 on) ----

    public void StartClock()
    {
        if (running) return;
        running = true;
        OnRunningChanged?.Invoke(true);
    }

    public void StopClock()
    {
        if (!running) return;
        running = false;
        OnRunningChanged?.Invoke(false);
    }

    public void ToggleClock()
    {
        if (running) StopClock(); else StartClock();
    }

    /// <summary>Stops the clock and returns it to the reset time.</summary>
    public void ResetClock()
    {
        StopClock();
        JumpTo(ToSeconds(resetHour, resetMinute, resetSecond));
        OnReset?.Invoke();
    }

    /// <summary>Jumps to a time without changing whether the clock is running.</summary>
    public bool SetTime(int hours, int minutes, int seconds)
    {
        if (hours < 0 || hours > 23 || minutes < 0 || minutes > 59 || seconds < 0 || seconds > 59)
        {
            Debug.LogWarning($"ClockController: invalid time {hours}:{minutes}:{seconds}");
            return false;
        }
        JumpTo(ToSeconds(hours, minutes, seconds));
        return true;
    }

    /// <summary>Accepts "11:59:50", "11 59 50" or "11:59".</summary>
    public bool SetTime(string text)
    {
        string[] parts = text.Split(new[] { ':', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        int s = 0;
        if (parts.Length >= 2 && parts.Length <= 3 &&
            int.TryParse(parts[0], out int h) &&
            int.TryParse(parts[1], out int m) &&
            (parts.Length == 2 || int.TryParse(parts[2], out s)))
        {
            return SetTime(h, m, s);
        }
        Debug.LogWarning($"ClockController: can't parse time \"{text}\"");
        return false;
    }

    /// <summary>Clock time as h:mm:ss on a 12-hour face.</summary>
    public string TimeString
    {
        get
        {
            long total = WholeSeconds;
            long h = total / 3600 % 12;
            if (h == 0) h = 12;
            return $"{h}:{total / 60 % 60:00}:{total % 60:00}";
        }
    }

    /// <summary>Back-style ease: 0 to 1 with a small overshoot past 1 before settling.</summary>
    public static float EaseTick(float t, float overshoot)
    {
        t = Mathf.Clamp01(t);
        float u = t - 1f;
        return 1f + (overshoot + 1f) * u * u * u + overshoot * u * u;
    }

    // ---- Internals ----

    void JumpTo(double seconds)
    {
        elapsedSeconds = seconds;
        fromTick = toTick = WholeSeconds;
        tickTimer = tickDuration; // no animation: snap straight there
        UpdateHands();
    }

    static double ToSeconds(int h, int m, int s) => (h % 12) * 3600 + m * 60 + s;

    void UpdateHands()
    {
        double ticks = fromTick + (toTick - fromTick) * (double)TickProgress;
        SetAngle(secondHand, ticks % 60 * 6.0);
        SetAngle(minuteHand, elapsedSeconds / 60 % 60 * 6.0);
        SetAngle(hourHand, elapsedSeconds / 3600 % 12 * 30.0);
    }

    static void SetAngle(Transform hand, double degrees)
    {
        // Positive Z rotates counter-clockwise in Unity; clock hands go clockwise.
        if (hand != null) hand.localRotation = Quaternion.Euler(0f, 0f, -(float)degrees);
    }

    void HandleDebugKeys()
    {
#if ENABLE_INPUT_SYSTEM
        Keyboard kb = Keyboard.current;
        if (kb == null) return;
        bool toggle = kb.spaceKey.wasPressedThisFrame;
        bool reset = kb.rKey.wasPressedThisFrame;
        bool jump = kb.tKey.wasPressedThisFrame;
#else
        bool toggle = Input.GetKeyDown(KeyCode.Space);
        bool reset = Input.GetKeyDown(KeyCode.R);
        bool jump = Input.GetKeyDown(KeyCode.T);
#endif
        if (toggle) ToggleClock();
        if (reset) ResetClock();
        if (jump) SetTime(debugJumpTime);
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

        var content = new GUIContent(
            $"{TimeString}  {(running ? "RUNNING" : "STOPPED")}    [Space] start/stop   [R] reset   [T] jump to {debugJumpTime}");
        Vector2 size = overlayStyle.CalcSize(content);
        GUI.Label(new Rect(10, 10, size.x, size.y), content, overlayStyle);
    }
}
