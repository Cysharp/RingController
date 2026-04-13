using System.Text.Json.Serialization;

namespace RingController;

[JsonConverter(typeof(JsonStringEnumConverter<RingDirection>))]
public enum RingDirection
{
    Left,
    Right
}

public enum RingActionKind
{
    None = 0,

    // Launch
    LaunchApp = 10,
    /// <summary> Send a broadcast with the given action (e.g. MacroDroid &quot;Intent Received&quot;). Uses <see cref="RingActionConfig.IntentAction"/>. </summary>
    BroadcastIntentAction = 11,
    /// <summary> Open a URL in the default browser (ACTION_VIEW). </summary>
    OpenUrl = 12,

    // Volume
    VolumeUp = 20,
    VolumeDown = 21,

    // Brightness
    BrightnessUp = 30,
    BrightnessDown = 31,

    // Media transport
    MediaPlayPause = 40,
    MediaStop = 41,
    MediaNext = 42,
    MediaPrev = 43,
    MediaFastForward = 44,
    MediaRewind = 45,

    // Global actions
    Screenshot = 50,
    LockScreen = 51,
    Flashlight = 52,
    RotationLock = 54,

    // Touch simulation via AccessibilityService (e.g. Kindle page turns)
    TapLeftEdge = 60,
    TapRightEdge = 61,
    SwipeLeftFromCenter = 62,
    SwipeRightFromCenter = 63,
    DoubleTapLeftEdge = 64,
    DoubleTapRightEdge = 65,

    /// <summary> Pinch in from screen edges toward center (AccessibilityService gesture injection). </summary>
    PinchIn = 66,
    /// <summary> Pinch out from center toward screen edges (AccessibilityService gesture injection). </summary>
    PinchOut = 67,

    /// <summary> Vertical swipe: finger moves up from mid-screen (typical scroll-down content). </summary>
    SwipeUpFromCenter = 68,
    /// <summary> Vertical swipe: finger moves down from mid-screen (typical scroll-up content). </summary>
    SwipeDownFromCenter = 69,
}

[JsonConverter(typeof(JsonStringEnumConverter<RingExecutionMode>))]
public enum RingExecutionMode
{
    // Fire on every sensor event using current delta_x.
    EachEvent = 0,

    // Fire when blended magnitude meets MinAbsToTrigger; min spacing between fires = AccumulationTimeoutMs (sum/delta blend reduces jitter).
    ThresholdCrossing = 1,

    // Baseline on sum_delta_x; effective strength max(|sum−baseline|, Threshold-style blended |sum|/|delta|); same spacing/idle timeout as today.
    ThresholdAccumulatedRepeat = 2,

    // Gesture patterns only (no per-direction default mapping).
    Gesture = 3,
}

public sealed class RingActionConfig
{
    [JsonConverter(typeof(RingActionKindJsonConverter))]
    public RingActionKind Kind { get; set; } = RingActionKind.None;

    // For LaunchApp
    public string? LaunchPackageName { get; set; }

    /// <summary> For <see cref="RingActionKind.BroadcastIntentAction"/>: broadcast action string (e.g. <c>ring.receive</c>). </summary>
    public string? IntentAction { get; set; }

    // For OpenUrl
    public string? UrlString { get; set; }

    // For Volume only: number of stream volume steps per trigger.
    public int? Steps { get; set; }
}

public sealed class RingMagnitudeActionRule
{
    public int MinAbs { get; set; }
    public int? MaxAbs { get; set; }
    public RingActionConfig Action { get; set; } = new();
}

public sealed class RingDirectionMappingConfig
{
    public RingActionConfig DefaultAction { get; set; } = new() { Kind = RingActionKind.None };
    public List<RingMagnitudeActionRule> MagnitudeRules { get; set; } = new();

    public RingActionConfig GetActionForAbs(int absDeltaX)
    {
        RingMagnitudeActionRule? best = null;
        foreach (var r in MagnitudeRules)
        {
            if (absDeltaX < r.MinAbs) continue;
            if (r.MaxAbs != null && absDeltaX > r.MaxAbs.Value) continue;
            if (best == null || r.MinAbs >= best.MinAbs) best = r;
        }
        return (best?.Action?.Kind != null && best.Action.Kind != RingActionKind.None)
            ? best.Action
            : DefaultAction;
    }
}

public sealed class RingSequenceStepConfig
{
    public RingDirection Direction { get; set; } = RingDirection.Right;
    public int MinAbs { get; set; }
    public int? MaxAbs { get; set; }
}

public sealed class RingSequenceRuleConfig
{
    public List<RingSequenceStepConfig> Steps { get; set; } = new();

    // MaxGapMs: sensor debounce (Gesture). MaxTotalMs: optional cap from first step done (0 = off). UI timeout maps to MaxGapMs only.
    public long MaxGapMs { get; set; } = 1000;
    public long MaxTotalMs { get; set; } = 1000;

    public RingActionConfig Action { get; set; } = new();
}

public sealed class RingContextConfig
{
    public RingDirectionMappingConfig Left { get; set; } = new();
    public RingDirectionMappingConfig Right { get; set; } = new();
    public List<RingSequenceRuleConfig> Sequences { get; set; } = new();
}

/// <summary> Thresholds and actions (left/right, sequences) for Each / Threshold / Accumulate modes. </summary>
public sealed class RingModeProfile
{
    /// <summary> Ignored when absDeltaX passed to Interpret is below this (EachEvent: mainly for sequence-step interpretation). </summary>
    public int MinAbsToTrigger { get; set; } = 10;

    /// <summary> ThresholdAccumulatedRepeat: if input is idle for this many ms, align baseline to the current sum / min interval between repeats. ThresholdCrossing: min interval between repeats (ms). </summary>
    public long AccumulationTimeoutMs { get; set; } = 100;

    public long ActionCooldownMs { get; set; } = 0;

    public long SequenceBufferWindowMs { get; set; } = 1400;

    /// <summary> Shared <see cref="RingSequenceRuleConfig.MaxGapMs"/> for all gesture sequences in this profile (Gesture tab). </summary>
    public long GestureSequenceTimeoutMs { get; set; } = 300;

    public RingContextConfig Normal { get; set; } = new();

    /// <summary> Each tab defaults: left/right volume; Trigger/Timeout kept low as before. </summary>
    public static RingModeProfile CreateDefaultEachMode() => new()
    {
        MinAbsToTrigger = 10,
        AccumulationTimeoutMs = 100,
        ActionCooldownMs = 0,
        SequenceBufferWindowMs = 1400,
        GestureSequenceTimeoutMs = 300,
        Normal = new RingContextConfig
        {
            Left = new RingDirectionMappingConfig
            {
                DefaultAction = new RingActionConfig { Kind = RingActionKind.VolumeDown, Steps = 1 }
            },
            Right = new RingDirectionMappingConfig
            {
                DefaultAction = new RingActionConfig { Kind = RingActionKind.VolumeUp, Steps = 1 }
            },
            Sequences = new List<RingSequenceRuleConfig>()
        },
    };

    /// <summary> Accumulate tab defaults: Trigger 300, Timeout 300, no left/right mapping. </summary>
    public static RingModeProfile CreateDefaultAccumulateMode() => new()
    {
        MinAbsToTrigger = 300,
        AccumulationTimeoutMs = 300,
        ActionCooldownMs = 0,
        SequenceBufferWindowMs = 1400,
        GestureSequenceTimeoutMs = 300,
        Normal = new RingContextConfig
        {
            Left = new RingDirectionMappingConfig { DefaultAction = new RingActionConfig { Kind = RingActionKind.None } },
            Right = new RingDirectionMappingConfig { DefaultAction = new RingActionConfig { Kind = RingActionKind.None } },
            Sequences = new List<RingSequenceRuleConfig>()
        },
    };

    /// <summary> Threshold tab defaults: Trigger 500, Timeout 300, no left/right mapping. </summary>
    public static RingModeProfile CreateDefaultThresholdMode() => new()
    {
        MinAbsToTrigger = 500,
        AccumulationTimeoutMs = 300,
        ActionCooldownMs = 0,
        SequenceBufferWindowMs = 1400,
        GestureSequenceTimeoutMs = 300,
        Normal = new RingContextConfig
        {
            Left = new RingDirectionMappingConfig { DefaultAction = new RingActionConfig { Kind = RingActionKind.None } },
            Right = new RingDirectionMappingConfig { DefaultAction = new RingActionConfig { Kind = RingActionKind.None } },
            Sequences = new List<RingSequenceRuleConfig>()
        },
    };

    /// <summary> Legacy/migration; currently same as <see cref="CreateDefaultEachMode"/>. </summary>
    public static RingModeProfile CreateDefault() => CreateDefaultEachMode();
}

public sealed class RingConfig
{
    public RingExecutionMode ExecutionMode { get; set; } = RingExecutionMode.EachEvent;

    /// <summary>
    /// When true, ring actions run even if the device is locked (keyguard). Controlled from Settings; default off.
    /// </summary>
    public bool RunWhenDeviceLocked { get; set; }

    /// <summary>
    /// Per-foreground-app overrides. When a package has an entry, the service uses that config
    /// instead of the global fields above (nested <see cref="PerAppOverrides"/> on entries are ignored).
    /// </summary>
    public Dictionary<string, RingConfig> PerAppOverrides { get; set; } = new();

    public RingModeProfile EachMode { get; set; } = RingModeProfile.CreateDefaultEachMode();
    public RingModeProfile ThresholdMode { get; set; } = RingModeProfile.CreateDefaultThresholdMode();
    public RingModeProfile AccumulateMode { get; set; } = RingModeProfile.CreateDefaultAccumulateMode();

    /// <summary> Settings for <see cref="RingExecutionMode.Gesture"/> (sequences only at runtime). </summary>
    public RingModeProfile GestureMode { get; set; } = CreateDefaultGestureMode();

    public RingModeProfile GetProfile(RingExecutionMode mode) => mode switch
    {
        RingExecutionMode.EachEvent => EachMode,
        RingExecutionMode.ThresholdCrossing => ThresholdMode,
        RingExecutionMode.Gesture => GestureMode,
        _ => AccumulateMode,
    };

    /// <summary> Typical system camera package; used as default Launch App target for gesture presets. </summary>
    public const string DefaultCameraLaunchPackageName = "com.android.camera";

    /// <summary> Left 200 → Right 200 → Left 200, launch camera (Gesture tab default). </summary>
    public static RingSequenceRuleConfig CreateDefaultGestureSequenceRule() => new()
    {
        Steps =
        [
            new RingSequenceStepConfig { Direction = RingDirection.Left, MinAbs = 200 },
            new RingSequenceStepConfig { Direction = RingDirection.Right, MinAbs = 200 },
            new RingSequenceStepConfig { Direction = RingDirection.Left, MinAbs = 200 },
        ],
        MaxGapMs = 300,
        MaxTotalMs = 0,
        Action = new RingActionConfig
        {
            Kind = RingActionKind.LaunchApp,
            LaunchPackageName = DefaultCameraLaunchPackageName
        }
    };

    /// <summary> Gesture tab defaults: Timeout 300, L200→R200→L200 launches camera, no left/right mapping. </summary>
    public static RingModeProfile CreateDefaultGestureMode() => new()
    {
        MinAbsToTrigger = 10,
        AccumulationTimeoutMs = 100,
        ActionCooldownMs = 0,
        SequenceBufferWindowMs = 1400,
        GestureSequenceTimeoutMs = 300,
        Normal = new RingContextConfig
        {
            Left = new RingDirectionMappingConfig { DefaultAction = new RingActionConfig { Kind = RingActionKind.None } },
            Right = new RingDirectionMappingConfig { DefaultAction = new RingActionConfig { Kind = RingActionKind.None } },
            Sequences = [CreateDefaultGestureSequenceRule()]
        }
    };

    public static RingConfig CreateDefault()
    {
        return new RingConfig
        {
            ExecutionMode = RingExecutionMode.EachEvent,
            EachMode = RingModeProfile.CreateDefaultEachMode(),
            ThresholdMode = RingModeProfile.CreateDefaultThresholdMode(),
            AccumulateMode = RingModeProfile.CreateDefaultAccumulateMode(),
            GestureMode = CreateDefaultGestureMode()
        };
    }

    /// <summary>
    /// True if any profile (each/threshold/accumulate/gesture) or per-app override assigns Brightness Up/Down.
    /// </summary>
    public static bool ConfigUsesBrightnessControl(RingConfig cfg)
    {
        if (ProfileUsesBrightness(cfg.EachMode)) return true;
        if (ProfileUsesBrightness(cfg.ThresholdMode)) return true;
        if (ProfileUsesBrightness(cfg.AccumulateMode)) return true;
        if (ProfileUsesBrightness(cfg.GestureMode)) return true;
        foreach (var kv in cfg.PerAppOverrides)
        {
            if (ConfigUsesBrightnessControl(kv.Value)) return true;
        }
        return false;
    }

    static bool ProfileUsesBrightness(RingModeProfile profile)
    {
        var n = profile.Normal;
        if (DirectionUsesBrightness(n.Left) || DirectionUsesBrightness(n.Right)) return true;
        foreach (var seq in n.Sequences)
        {
            if (ActionUsesBrightness(seq.Action)) return true;
        }
        return false;
    }

    static bool DirectionUsesBrightness(RingDirectionMappingConfig map)
    {
        if (ActionUsesBrightness(map.DefaultAction)) return true;
        foreach (var r in map.MagnitudeRules)
        {
            if (ActionUsesBrightness(r.Action)) return true;
        }
        return false;
    }

    static bool ActionUsesBrightness(RingActionConfig a) =>
        a.Kind is RingActionKind.BrightnessUp or RingActionKind.BrightnessDown;
}
