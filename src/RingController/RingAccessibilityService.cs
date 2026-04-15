using Android.AccessibilityServices;
using Android.Content;
using Android.Hardware;
using Android.Media;
using Android.Provider;
using Android.Runtime;
using Android.Util;
using Android.OS;
using Android.Views.Accessibility;

namespace RingController;

[Service(
    Permission = "android.permission.BIND_ACCESSIBILITY_SERVICE",
    Exported = false,
    ForegroundServiceType = Android.Content.PM.ForegroundService.TypeSpecialUse)]
[IntentFilter(new[] { "android.accessibilityservice.AccessibilityService" })]
[MetaData("android.accessibilityservice", Resource = "@xml/accessibility_service_config")]
public class RingAccessibilityService : AccessibilityService, ISensorEventListener
{
    public static bool IsRunning { get; private set; }

    SensorManager? sensorManager;
    RingSensorModel? ringSensor;
    AudioManager? audioManager;
    KeyguardManager? keyguardManager;
    bool isCameraForeground;

    /// <summary> Resolved foreground package (after MIUI home / system UI handling). Used for per-app overrides. </summary>
    string? foregroundResolvedPackage;

    readonly RingGestureInterpreter interpreter = new();
    RingActionExecutor? executor;

    // When sum_delta_x is 0, direction is ambiguous; keep last direction (threshold crossing).
    RingDirection lastDirection = RingDirection.Right;

    // ThresholdAccumulatedRepeat: |sum - baseline|; baseline advances by MinAbsToTrigger per fire (same |sum| view as Threshold).
    int? accumBaselineSum;
    long lastSensorEventMsForAccum;
    int? lastSumDeltaXForAccum;
    long lastAccumExecuteAtMs;

    /// <summary> ThresholdCrossing: last time we executed after a successful <see cref="RingGestureInterpreter.Interpret"/> (rate limit via <see cref="RingModeProfile.AccumulationTimeoutMs"/>). </summary>
    long lastThresholdTriggerAtMs;

    /// <summary> Last sample where ring reported motion or off-center integral (see <see cref="TryResetInterpreterAfterRingIdle"/>). </summary>
    long lastMeaningfulRingActivityMs;

    const long MeaningfulRingIdleResetMs = 400;
    const int MeaningfulRingSumAbsQuiet = 24;

    const string SensorStringTypeOpticalTracking = "xiaomi.sensor.optical_tracking";

    public override void OnCreate()
    {
        base.OnCreate();
        audioManager = (AudioManager?)GetSystemService(AudioService);
        sensorManager = (SensorManager?)GetSystemService(SensorService);
        keyguardManager = (KeyguardManager?)GetSystemService(KeyguardService);

        ringSensor = RingSensorModel.GetSensor(sensorManager);

        executor = new RingActionExecutor(this);
    }

    protected override void OnServiceConnected()
    {
        base.OnServiceConnected();
        IsRunning = true;

        // Foreground service
        var channelId = "ring_controller_channel";
        var channel = new NotificationChannel(channelId, "Ring Controller",
            NotificationImportance.Min);
        var notificationManager = GetSystemService(NotificationService) as NotificationManager;
        notificationManager?.CreateNotificationChannel(channel);

        var notification = new Notification.Builder(this, channelId)
            .SetContentTitle("Ring Controller")
            .SetContentText("Running")
            .SetSmallIcon(Resource.Drawable.ic_stat_ring)
            .SetOngoing(true)
            .Build();

        StartForeground(1, notification);

        // Do not call SetServiceInfo with a minimal AccessibilityServiceInfo — that would drop
        // manifest capabilities (e.g. canPerformGestures) and break AccessibilityService.dispatchGesture.
        // @xml/accessibility_service_config drives ServiceInfo.

        // Sensor
        if (ringSensor != null)
        {
            sensorManager!.RegisterListener(this, ringSensor.Sensor, SensorDelay.Fastest);
        }

        UpdateForegroundPackage(RootInActiveWindow?.PackageName ?? "");
    }

    public override void OnDestroy()
    {
        IsRunning = false;
        sensorManager?.UnregisterListener(this);
        base.OnDestroy();
    }

    /// <summary> Sets foreground package for per-app config and whether a camera app is on top. </summary>
    void UpdateForegroundPackage(string packageName)
    {
        var priorResolved = foregroundResolvedPackage;
        var resolvedPackageName = packageName;

        // Home / system UI can be the event package while another app is actually focused — prefer root window.
        switch (resolvedPackageName)
        {
            case "com.miui.home":
            case "com.android.systemui":
            case "miui.systemui.plugin":
            case "com.mi.appfinder":
                var actualPkg = RootInActiveWindow?.PackageName;
                if (actualPkg == null)
                {
                    foregroundResolvedPackage = resolvedPackageName;
                    return;
                }

                resolvedPackageName = actualPkg!;
                break;
            default:
                break;
        }

        switch (resolvedPackageName)
        {
            case "com.leica.camera":
            case "com.xiaomi.camera":
            case "com.android.camera":
                isCameraForeground = true;
                break;
            default:
                isCameraForeground = false;
                break;
        }

        foregroundResolvedPackage = resolvedPackageName;
        if (!string.IsNullOrEmpty(priorResolved) &&
            !string.Equals(priorResolved, foregroundResolvedPackage, StringComparison.Ordinal))
        {
            interpreter.ResetProcessingStateAfterIdle();
            lastSumDeltaXForAccum = null;
            accumBaselineSum = null;
            lastMeaningfulRingActivityMs = 0;
        }
    }

    RingConfig ResolveEffectiveRingConfig(RingConfig? loadedRoot = null)
    {
        var root = loadedRoot ?? RingConfigStore.LoadOrCreate(this);
        if (!string.IsNullOrEmpty(foregroundResolvedPackage) &&
            root.PerAppOverrides.TryGetValue(foregroundResolvedPackage, out var ov) &&
            ov != null)
            return ov;
        return root;
    }

    /// <summary> Same blended magnitude as ThresholdCrossing: prefer <c>|sum|</c> once large, else <c>max(|sum|,|delta|)</c>. </summary>
    static int ThresholdAlignedBlendedAbs(int sum, int delta, int minAbsToTrigger)
    {
        var absSum = Math.Abs(sum);
        var absDelta = Math.Abs(delta);
        var sumDominatesAt = Math.Max(24, Math.Min(56, minAbsToTrigger * 2 + 8));
        return absSum >= sumDominatesAt ? absSum : Math.Max(absSum, absDelta);
    }

    static void ThresholdAlignedUpdateDirection(int sum, int delta, ref RingDirection lastDirection)
    {
        var absSum = Math.Abs(sum);
        var absDelta = Math.Abs(delta);
        if (absSum >= absDelta && sum != 0)
            lastDirection = sum > 0 ? RingDirection.Left : RingDirection.Right;
        else if (delta != 0)
            lastDirection = delta > 0 ? RingDirection.Left : RingDirection.Right;
    }

    /// <summary>
    /// Accumulate: strength must follow <c>|sum − baseline|</c> so each MinAbs step consumes real integral (e.g. +3000 / 300 → 10 fires).
    /// When <c>|sum|</c> is still small, also take <see cref="ThresholdAlignedBlendedAbs"/> so single-tick delta spikes match Threshold.
    /// </summary>
    static int AccumulateEffectiveAbsMag(int sum, int delta, int baseline, int minAbsToTrigger)
    {
        var absSum = Math.Abs(sum);
        var sumDominatesAt = Math.Max(24, Math.Min(56, minAbsToTrigger * 2 + 8));
        var integralAbs = Math.Abs(sum - baseline);
        if (absSum >= sumDominatesAt)
            return integralAbs;
        return Math.Max(integralAbs, ThresholdAlignedBlendedAbs(sum, delta, minAbsToTrigger));
    }

    /// <summary>
    /// Optical tracking can stream events continuously; inter-sample time never shows "idle". After quiet motion/integral,
    /// reset interpreter + accum baselines so stale driver state cannot block triggers.
    /// </summary>
    void TryResetInterpreterAfterRingIdle(long nowMs, int sumDeltaX, int deltaX)
    {
        var meaningful = deltaX != 0 || Math.Abs(sumDeltaX) > MeaningfulRingSumAbsQuiet;
        if (meaningful)
        {
            lastMeaningfulRingActivityMs = nowMs;
            return;
        }

        if (lastMeaningfulRingActivityMs <= 0) return;
        if (nowMs - lastMeaningfulRingActivityMs <= MeaningfulRingIdleResetMs) return;

        interpreter.ResetProcessingStateAfterIdle();
        lastSumDeltaXForAccum = null;
        accumBaselineSum = null;
        lastMeaningfulRingActivityMs = 0;
    }

    public void OnSensorChanged(SensorEvent? e)
    {
        if (e?.Values == null || e.Values.Count < 8) return;
        if (ringSensor == null) return;

        var root = RingConfigStore.LoadOrCreate(this);
        if (keyguardManager?.IsDeviceLocked == true && !root.RunWhenDeviceLocked) return;
        if (isCameraForeground) return;

        var sensorData = ringSensor.CreateRingSensorData(e);
        var now = SystemClock.UptimeMillis();
        TryResetInterpreterAfterRingIdle(now, sensorData.sum_delta_x, sensorData.delta_x);

        var config = ResolveEffectiveRingConfig(root);
        var modeProfile = config.GetProfile(config.ExecutionMode);

        // Timing state (threshold / accum idle / gesture gap & cooldown) must persist across sensor callbacks.
        // Resetting clocks every sample broke MaxGapMs, idle baseline snap, and cooldowns after pauses (e.g. app scroll).

        if (config.ExecutionMode != RingExecutionMode.ThresholdAccumulatedRepeat)
        {
            lastSumDeltaXForAccum = null;
            accumBaselineSum = null;
        }

        int abs;
        RingDirection direction;

        if (config.ExecutionMode == RingExecutionMode.Gesture)
        {
            var sum = sensorData.sum_delta_x;
            var gestureAction = interpreter.InterpretGesture(
                sumDeltaX: sum,
                deltaX: sensorData.delta_x,
                nowMs: now,
                modeProfile: modeProfile);

            if (gestureAction == null) return;

            abs = ThresholdAlignedBlendedAbs(sum, sensorData.delta_x, modeProfile.MinAbsToTrigger);
            Log.Debug("RingController", $"Execute action={gestureAction.Kind} sum={sum} gesture");
            executor?.Execute(gestureAction, abs);
            return;
        }

        if (config.ExecutionMode == RingExecutionMode.EachEvent)
        {
            var delta = sensorData.delta_x;
            if (delta == 0) return;

            direction = delta > 0 ? RingDirection.Left : RingDirection.Right;
            abs = Math.Abs(delta);
        }
        else if (config.ExecutionMode == RingExecutionMode.ThresholdAccumulatedRepeat)
        {
            long resetAfter = modeProfile.AccumulationTimeoutMs > 0 ? modeProfile.AccumulationTimeoutMs : 100;
            long minIntervalMs = resetAfter;

            var sum = sensorData.sum_delta_x;
            var delta = sensorData.delta_x;

            // Idle: snap baseline to current sum (same idea as clearing drift; avoids losing micro-steps to delta-sum resets).
            if (lastSensorEventMsForAccum > 0 && now - lastSensorEventMsForAccum > resetAfter)
            {
                accumBaselineSum = sum;
                lastSumDeltaXForAccum = sum;
            }

            lastSensorEventMsForAccum = now;

            if (!lastSumDeltaXForAccum.HasValue)
            {
                lastSumDeltaXForAccum = sum;
                accumBaselineSum = sum;
                return;
            }

            var prev = lastSumDeltaXForAccum.Value;
            var d = sum - prev;

            const int minPrevForDriverZero = 48;
            if (Math.Abs(sum) <= RingGestureInterpreter.DriverNeutralAbsBand && Math.Abs(prev) >= minPrevForDriverZero)
            {
                accumBaselineSum = sum;
                lastSumDeltaXForAccum = sum;
                return;
            }

            const int maxPlausibleStep = 32768;
            if (Math.Abs(d) > maxPlausibleStep)
            {
                accumBaselineSum = sum;
                lastSumDeltaXForAccum = sum;
                return;
            }

            lastSumDeltaXForAccum = sum;

            var baseline = accumBaselineSum ?? sum;
            accumBaselineSum = baseline;

            var accumSigned = sum - baseline;
            var absMag = AccumulateEffectiveAbsMag(sum, delta, baseline, modeProfile.MinAbsToTrigger);

            ThresholdAlignedUpdateDirection(sum, delta, ref lastDirection);
            direction = lastDirection;

            if (absMag < modeProfile.MinAbsToTrigger)
                return;

            // First iteration: rate limit vs previous callback. Further steps in the same callback drain
            // remaining |sum−baseline| (e.g. +3000 with MinAbs 300 → up to 10 executes here). Cap avoids runaway.
            const int maxStepsThisCallback = 50;
            for (var i = 0; i < maxStepsThisCallback && absMag >= modeProfile.MinAbsToTrigger; i++)
            {
                if (i == 0 && lastAccumExecuteAtMs > 0 && now - lastAccumExecuteAtMs < minIntervalMs)
                    break;

                var repeatAction = interpreter.Interpret(
                    absDeltaX: absMag,
                    direction: direction,
                    modeProfile: modeProfile,
                    executionMode: RingExecutionMode.ThresholdAccumulatedRepeat);

                if (repeatAction == null) break;

                Log.Debug("RingController", $"Execute action={repeatAction.Kind} abs={absMag} dir={direction} accumRepeat");
                executor?.Execute(repeatAction, absMag);

                lastAccumExecuteAtMs = now;

                var sign = accumSigned > 0 ? 1 : accumSigned < 0 ? -1 : (direction == RingDirection.Left ? 1 : -1);
                baseline += sign * modeProfile.MinAbsToTrigger;
                accumBaselineSum = baseline;
                accumSigned = sum - baseline;
                absMag = AccumulateEffectiveAbsMag(sum, delta, baseline, modeProfile.MinAbsToTrigger);
                ThresholdAlignedUpdateDirection(sum, delta, ref lastDirection);
                direction = lastDirection;
            }

            return;
        }
        else
        {
            var sum = sensorData.sum_delta_x;
            var delta = sensorData.delta_x;
            abs = ThresholdAlignedBlendedAbs(sum, delta, modeProfile.MinAbsToTrigger);
            ThresholdAlignedUpdateDirection(sum, delta, ref lastDirection);
            direction = lastDirection;
        }

        var skipThresholdInterpret = false;
        if (config.ExecutionMode == RingExecutionMode.ThresholdCrossing)
        {
            var minTrig = modeProfile.MinAbsToTrigger;
            if (abs < minTrig)
                skipThresholdInterpret = true;
            else
            {
                var nowGate = SystemClock.UptimeMillis();
                long minIntervalMs = modeProfile.AccumulationTimeoutMs > 0 ? modeProfile.AccumulationTimeoutMs : 100;
                if (lastThresholdTriggerAtMs > 0 && nowGate - lastThresholdTriggerAtMs < minIntervalMs)
                    skipThresholdInterpret = true;
            }
        }

        if (skipThresholdInterpret)
            return;

        var action = interpreter.Interpret(
            absDeltaX: abs,
            direction: direction,
            modeProfile: modeProfile,
            executionMode: config.ExecutionMode);

        if (action == null) return;

        if (config.ExecutionMode == RingExecutionMode.ThresholdCrossing)
            lastThresholdTriggerAtMs = SystemClock.UptimeMillis();

        Log.Debug("RingController", $"Execute action={action.Kind} abs={abs} dir={direction}");
        executor?.Execute(action, abs);
    }

    public override void OnAccessibilityEvent(AccessibilityEvent? e)
    {
        if (e == null) return;

        if (e.EventType == EventTypes.WindowStateChanged)
        {
            var packageName = e.PackageName;
            if (!string.IsNullOrEmpty(packageName))
                UpdateForegroundPackage(packageName);
        }
    }

    public override void OnInterrupt() { }

    public void OnAccuracyChanged(Sensor? sensor, [GeneratedEnum] SensorStatus accuracy)
    {
    }
}
