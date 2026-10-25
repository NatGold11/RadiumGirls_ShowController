using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>
/// The show's command router. Every action is a named command such as "clock/start"
/// or "media/play 4". The keyboard, the Stream Deck (OSC) and the operator UI all send
/// commands here, so each behaviour is written once.
/// </summary>
public class ShowController : MonoBehaviour
{
#if ENABLE_INPUT_SYSTEM
    [Serializable]
    public class KeyBinding
    {
        public Key key;
        public string command;

        public KeyBinding() { }
        public KeyBinding(Key key, string command)
        {
            this.key = key;
            this.command = command;
        }
    }
#endif

    public class CommandInfo
    {
        public string Name;
        public string Usage;
    }

    delegate bool Handler(string[] args);

    [Header("Components (found automatically if left empty)")]
    [SerializeField] ClockController clock;
    [SerializeField] MediaManager media;
    [SerializeField] OutputManager output;

    [Header("Keyboard (backup control; only works while this window has focus)")]
    [SerializeField] bool keyboardEnabled = true;
#if ENABLE_INPUT_SYSTEM
    [SerializeField] List<KeyBinding> keyBindings = new List<KeyBinding>
    {
        new KeyBinding(Key.Space, "clock/toggle"),
        new KeyBinding(Key.R, "clock/reset"),
        new KeyBinding(Key.T, "clock/set 11:59:50"),
        new KeyBinding(Key.N, "media/next"),
        new KeyBinding(Key.P, "media/prev"),
        new KeyBinding(Key.Digit1, "media/play 1"),
        new KeyBinding(Key.Digit2, "media/play 2"),
        new KeyBinding(Key.Digit3, "media/play 3"),
        new KeyBinding(Key.Digit4, "media/play 4"),
        new KeyBinding(Key.Digit5, "media/play 5"),
        new KeyBinding(Key.Digit6, "media/play 6"),
        new KeyBinding(Key.Digit7, "media/play 7"),
        new KeyBinding(Key.Digit8, "media/play 8"),
        new KeyBinding(Key.Digit9, "media/play 9"),
        new KeyBinding(Key.F5, "media/rescan"),
        new KeyBinding(Key.C, "calibrate/toggle"),
    };
#endif

    [Header("Quit")]
    [Tooltip("show/quit must be sent twice within this many seconds, so one stray press can't end the show.")]
    [SerializeField] float quitConfirmSeconds = 3f;

    [Header("Debug")]
    [SerializeField] bool logCommands = true;
    [Tooltip("Shows the last command and a box to type commands into.")]
    [SerializeField] bool debugOverlay = true;

    /// <summary>Fired after every command: the command text and whether it succeeded.</summary>
    public event Action<string, bool> OnCommand;

    readonly Dictionary<string, Handler> handlers = new Dictionary<string, Handler>(StringComparer.OrdinalIgnoreCase);
    readonly List<CommandInfo> commands = new List<CommandInfo>();
    readonly ConcurrentQueue<string> pending = new ConcurrentQueue<string>();

    float quitArmedUntil = -1f;
    string lastCommand = "";
    bool lastSucceeded;
    string typedCommand = "";
    bool commandBoxFocused;
    GUIStyle overlayStyle, fieldStyle;

    public IReadOnlyList<CommandInfo> Commands => commands;

    void Awake()
    {
        if (clock == null) clock = FindFirstObjectByType<ClockController>();
        if (media == null) media = FindFirstObjectByType<MediaManager>();
        if (output == null) output = FindFirstObjectByType<OutputManager>();
        RegisterCommands();
    }

    void Update()
    {
        while (pending.TryDequeue(out string command)) Execute(command);

        if (output != null) output.KeyboardBlocked = commandBoxFocused;

#if ENABLE_INPUT_SYSTEM
        if (keyboardEnabled && !commandBoxFocused) HandleKeyboard();
#endif
    }

    // ---- Commands ----

    void RegisterCommands()
    {
        if (clock != null)
        {
            Register("clock/start", "", _ => { clock.StartClock(); return true; });
            Register("clock/stop", "", _ => { clock.StopClock(); return true; });
            Register("clock/toggle", "", _ => { clock.ToggleClock(); return true; });
            Register("clock/reset", "", _ => { clock.ResetClock(); return true; });
            Register("clock/set", "H M S  or  H:M:S", args => clock.SetTime(string.Join(" ", args)));
        }
        else
        {
            Debug.LogWarning("ShowController: no ClockController in the scene, clock/* commands are disabled.");
        }

        if (media != null)
        {
            Register("media/next", "", _ => { media.Next(); return true; });
            Register("media/prev", "", _ => { media.Previous(); return true; });
            Register("media/play", "N", args => TryNumber(args, out int n) && media.Play(n));
            Register("media/cue", "N", args => TryNumber(args, out int n) && media.Cue(n));
            Register("media/rescan", "", _ => { media.Rescan(); return true; });
        }
        else
        {
            Debug.LogWarning("ShowController: no MediaManager in the scene, media/* commands are disabled.");
        }

        if (output != null)
        {
            Register("calibrate/toggle", "", _ => { output.ToggleCalibrating(); return true; });
            Register("calibrate/on", "", _ => { output.SetCalibrating(true); return true; });
            Register("calibrate/off", "", _ => { output.SetCalibrating(false); return true; });
            Register("calibrate/save", "", _ => { output.SaveCalibration(); return true; });
        }
        else
        {
            Debug.LogWarning("ShowController: no OutputManager in the scene, calibrate/* commands are disabled.");
        }

        if (clock != null && media != null)
        {
            Register("show/reset", "", _ =>
            {
                clock.ResetClock();
                return media.Play(1);
            });
        }

        Register("show/quit", "send twice to confirm", _ => Quit());
    }

    /// <summary>The first show/quit only arms it; a second one within quitConfirmSeconds closes the app.</summary>
    bool Quit()
    {
        if (Time.unscaledTime > quitArmedUntil)
        {
            quitArmedUntil = Time.unscaledTime + quitConfirmSeconds;
            Debug.LogWarning($"ShowController: send show/quit again within {quitConfirmSeconds:0} seconds to close the show.");
            return true;
        }

        if (output != null) output.SetCalibrating(false); // saves calibration if it was open
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#else
        Application.Quit();
#endif
        return true;
    }

    void Register(string name, string usage, Handler handler)
    {
        handlers[name] = handler;
        commands.Add(new CommandInfo { Name = name, Usage = usage });
    }

    /// <summary>
    /// Runs a command such as "media/play 4" or "/clock/set 11 59 50" right now.
    /// Main thread only; use Enqueue from other threads (OSC receivers).
    /// </summary>
    public bool Execute(string commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return false;

        string[] parts = commandLine.Trim().Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
        string name = parts[0].Trim('/');
        string[] args = new string[parts.Length - 1];
        Array.Copy(parts, 1, args, 0, args.Length);

        bool succeeded;
        if (!handlers.TryGetValue(name, out Handler handler))
        {
            Debug.LogWarning($"ShowController: unknown command \"{name}\".");
            succeeded = false;
        }
        else
        {
            try
            {
                succeeded = handler(args);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                succeeded = false;
            }
        }

        lastCommand = string.Join(" ", parts);
        lastSucceeded = succeeded;
        if (logCommands) Debug.Log($"ShowController: {lastCommand} {(succeeded ? "(ok)" : "(FAILED)")}");
        OnCommand?.Invoke(lastCommand, succeeded);
        return succeeded;
    }

    /// <summary>Queues a command to run on the next frame. Safe to call from any thread.</summary>
    public void Enqueue(string commandLine) => pending.Enqueue(commandLine);

    static bool TryNumber(string[] args, out int number)
    {
        // Accepts "4" and "4.0", since some OSC senders only send floats.
        number = 0;
        if (args.Length > 0 && double.TryParse(args[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
        {
            number = (int)Math.Round(value);
            return true;
        }
        Debug.LogWarning("ShowController: this command needs a number, e.g. media/play 3");
        return false;
    }

    // ---- Keyboard ----

#if ENABLE_INPUT_SYSTEM
    void HandleKeyboard()
    {
        Keyboard kb = Keyboard.current;
        if (kb == null) return;
        // Calibration mode uses the keyboard itself, so only calibrate/* shortcuts stay live.
        bool calibrating = output != null && output.IsCalibrating;
        foreach (KeyBinding binding in keyBindings)
        {
            if (binding.key == Key.None || string.IsNullOrWhiteSpace(binding.command)) continue;
            if (calibrating && !binding.command.TrimStart('/').StartsWith("calibrate/", StringComparison.OrdinalIgnoreCase)) continue;
            if (kb[binding.key].wasPressedThisFrame) Execute(binding.command);
        }
    }

    string KeyHints()
    {
        var hints = new List<string>();
        bool hasPlayKeys = false;
        foreach (KeyBinding binding in keyBindings)
        {
            if (binding.key == Key.None) continue;
            if (binding.command.StartsWith("media/play ", StringComparison.OrdinalIgnoreCase)) hasPlayKeys = true;
            else hints.Add($"[{binding.key}] {binding.command}");
        }
        if (hasPlayKeys) hints.Add("[digits] media/play N");
        return string.Join("   ", hints);
    }
#else
    string KeyHints() => "(keyboard needs the Input System package)";
#endif

    // ---- Debug overlay ----

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
            fieldStyle = new GUIStyle(GUI.skin.textField) { fontSize = 22 };
        }

        string last = lastCommand.Length == 0 ? "none" : $"{lastCommand}  {(lastSucceeded ? "(ok)" : "(FAILED - see Console)")}";
        if (Time.unscaledTime <= quitArmedUntil) last += "   << send show/quit again to close >>";
        var content = new GUIContent($"Last command: {last}\n{KeyHints()}");
        Vector2 size = overlayStyle.CalcSize(content);
        GUI.Label(new Rect(10, 110, size.x, size.y), content, overlayStyle);

        // Type a command and press Enter. Esc leaves the box so the keyboard shortcuts work again.
        const string fieldName = "ShowCommandBox";
        float y = 120 + size.y;
        Event e = Event.current;
        if (e.type == EventType.KeyDown && GUI.GetNameOfFocusedControl() == fieldName)
        {
            if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter)
            {
                Enqueue(typedCommand);
                typedCommand = "";
                e.Use();
            }
            else if (e.keyCode == KeyCode.Escape)
            {
                GUI.FocusControl(null);
                e.Use();
            }
        }

        GUI.Label(new Rect(10, y, 140, 36), "Command:", overlayStyle);
        GUI.SetNextControlName(fieldName);
        typedCommand = GUI.TextField(new Rect(155, y, 500, 36), typedCommand, fieldStyle);
        commandBoxFocused = GUI.GetNameOfFocusedControl() == fieldName;
    }
}
