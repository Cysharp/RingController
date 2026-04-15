using Android.AccessibilityServices;
using Android.Content;
using Android.Content.PM;
using Android.Hardware.Camera2;
using Android.Hardware.Display;
using Android.Media;
using Android.OS;
using Android.Provider;
using Android.Util;
using Android.Views;

namespace RingController;

public sealed class RingActionExecutor
{
    /// <summary> Matches volume: 15 triggers span full range (255/15, same fraction as 100/15 for percent UI). </summary>
    const int BrightnessDeltaUnits = 17;

    /// <summary> Short stroke for injected edge taps (AccessibilityService gesture). </summary>
    const long EdgeTapStrokeMs = 68;

    /// <summary> Fast horizontal swipe (center); lower = snappier. </summary>
    const long SwipeGestureDurationMs = 95;

    /// <summary> Duration for center pinch gesture (AccessibilityService multi-stroke). </summary>
    const long PinchGestureDurationMs = 120;

    /// <summary> Delay from first tap start to second tap start (double-tap on edge). </summary>
    const long DoubleTapSecondStartDelayMs = 118;

    /// <summary> Last torch on/off applied by this executor (runtime state; toggled each trigger). </summary>
    bool torchFallbackOn;

    readonly AccessibilityService accessibilityService;
    readonly AudioManager? audioManager;
    readonly PackageManager packageManager;
    readonly Handler gestureHandler;

    public RingActionExecutor(AccessibilityService service)
    {
        accessibilityService = service;
        audioManager = (AudioManager?)service.GetSystemService(Context.AudioService);
        packageManager = service.PackageManager!;
        var looper = service.MainLooper ?? Looper.MainLooper;
        gestureHandler = looper != null ? new Handler(looper) : new Handler(Looper.MainLooper!);
    }

    public void Execute(RingActionConfig action, int absDeltaX)
    {
        try
        {
            switch (action.Kind)
            {
                case RingActionKind.None:
                    return;

                // Launch
                case RingActionKind.LaunchApp:
                    LaunchApp(action.LaunchPackageName);
                    return;
                case RingActionKind.BroadcastIntentAction:
                    SendBroadcastByAction(action.IntentAction);
                    return;
                case RingActionKind.OpenUrl:
                    OpenUrl(action.UrlString);
                    return;

                // Volume
                case RingActionKind.VolumeUp:
                    AdjustVolume(isRaise: true, steps: action.Steps ?? 1);
                    return;
                case RingActionKind.VolumeDown:
                    AdjustVolume(isRaise: false, steps: action.Steps ?? 1);
                    return;

                // Brightness
                case RingActionKind.BrightnessUp:
                    AdjustBrightness(delta: BrightnessDeltaUnits);
                    return;
                case RingActionKind.BrightnessDown:
                    AdjustBrightness(delta: -BrightnessDeltaUnits);
                    return;

                // Media transport via media key events (least permission friction)
                case RingActionKind.MediaPlayPause:
                    DispatchMediaKey(Keycode.MediaPlayPause);
                    return;
                case RingActionKind.MediaStop:
                    DispatchMediaKey(Keycode.MediaStop);
                    return;
                case RingActionKind.MediaNext:
                    DispatchMediaKey(Keycode.MediaNext);
                    return;
                case RingActionKind.MediaPrev:
                    DispatchMediaKey(Keycode.MediaPrevious);
                    return;
                case RingActionKind.MediaFastForward:
                    DispatchMediaKey(Keycode.MediaFastForward);
                    return;
                case RingActionKind.MediaRewind:
                    DispatchMediaKey(Keycode.MediaRewind);
                    return;

                // Global actions via accessibility
                case RingActionKind.Screenshot:
                    accessibilityService.PerformGlobalAction(Android.AccessibilityServices.GlobalAction.TakeScreenshot);
                    return;
                case RingActionKind.LockScreen:
                    accessibilityService.PerformGlobalAction(Android.AccessibilityServices.GlobalAction.LockScreen);
                    return;

                case RingActionKind.Flashlight:
                    ToggleFlashlight();
                    return;
                case RingActionKind.RotationLock:
                    ToggleRotationLock();
                    return;

                case RingActionKind.TapLeftEdge:
                    DispatchEdgeTap(isLeft: true);
                    return;
                case RingActionKind.TapRightEdge:
                    DispatchEdgeTap(isLeft: false);
                    return;
                case RingActionKind.SwipeLeftFromCenter:
                    DispatchCenterSwipe(isSwipeLeft: true);
                    return;
                case RingActionKind.SwipeRightFromCenter:
                    DispatchCenterSwipe(isSwipeLeft: false);
                    return;
                case RingActionKind.SwipeUpFromCenter:
                    DispatchCenterVerticalSwipe(swipeUp: true);
                    return;
                case RingActionKind.SwipeDownFromCenter:
                    DispatchCenterVerticalSwipe(swipeUp: false);
                    return;
                case RingActionKind.DoubleTapLeftEdge:
                    DispatchEdgeDoubleTap(isLeft: true);
                    return;
                case RingActionKind.DoubleTapRightEdge:
                    DispatchEdgeDoubleTap(isLeft: false);
                    return;

                case RingActionKind.PinchIn:
                    DispatchCenterPinch(pinchOut: false);
                    return;
                case RingActionKind.PinchOut:
                    DispatchCenterPinch(pinchOut: true);
                    return;

                default:
                    Log.Warn("RingActionExecutor", $"Unknown action kind: {action.Kind}");
                    return;
            }
        }
        catch (Exception ex)
        {
            Log.Warn("RingActionExecutor", $"Execute failed: {ex}");
        }
    }

    void LaunchApp(string? packageName)
    {
        if (string.IsNullOrEmpty(packageName)) return;

        var intent = packageManager.GetLaunchIntentForPackage(packageName);
        if (intent == null) return;

        intent.AddFlags(ActivityFlags.NewTask);
        accessibilityService.StartActivity(intent);
    }

    void SendBroadcastByAction(string? action)
    {
        if (string.IsNullOrWhiteSpace(action)) return;
        try
        {
            var intent = new Intent(action.Trim());
            accessibilityService.SendBroadcast(intent);
        }
        catch (Exception ex)
        {
            Log.Warn("RingActionExecutor", "SendBroadcastByAction: " + ex.Message);
        }
    }

    void OpenUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        var s = url.Trim();
        if (s.IndexOf("://", StringComparison.Ordinal) < 0)
            s = "https://" + s;
        try
        {
            var uri = Android.Net.Uri.Parse(s);
            var intent = new Intent(Intent.ActionView);
            intent.SetData(uri);
            intent.AddFlags(ActivityFlags.NewTask);
            accessibilityService.StartActivity(intent);
        }
        catch (Exception ex)
        {
            Log.Warn("RingActionExecutor", "OpenUrl: " + ex.Message);
        }
    }

    void ToggleFlashlight()
    {
        var cm = accessibilityService.GetSystemService(Context.CameraService) as CameraManager;
        if (cm == null) return;

        string? cameraId = null;
        foreach (var id in cm.GetCameraIdList() ?? [])
        {
            var ch = cm.GetCameraCharacteristics(id);
            var flash = ch.Get(CameraCharacteristics.FlashInfoAvailable);
            if (flash != null && (bool)flash)
            {
                cameraId = id;
                break;
            }
        }
        if (cameraId == null) return;

        // CameraManager.getTorchMode is not exposed on all .NET Android bindings; toggle local state.
        var turnOn = !torchFallbackOn;

        try
        {
            cm.SetTorchMode(cameraId, turnOn);
            torchFallbackOn = turnOn;
        }
        catch (Exception ex)
        {
            Log.Warn("RingActionExecutor", "ToggleFlashlight: " + ex.Message);
        }
    }

    void ToggleRotationLock()
    {
        if (!Settings.System.CanWrite(accessibilityService)) return;
        try
        {
            var resolver = accessibilityService.ContentResolver;
            var v = Settings.System.GetInt(resolver, Settings.System.AccelerometerRotation, 1);
            Settings.System.PutInt(resolver, Settings.System.AccelerometerRotation, v == 1 ? 0 : 1);
        }
        catch (Exception ex)
        {
            Log.Warn("RingActionExecutor", "ToggleRotationLock: " + ex.Message);
        }
    }

    void AdjustVolume(bool isRaise, int steps)
    {
        if (audioManager == null) return;

        steps = Math.Max(1, steps);
        var direction = isRaise ? Adjust.Raise : Adjust.Lower;

        for (int i = 0; i < steps; i++)
        {
            audioManager.AdjustStreamVolume(
                Android.Media.Stream.Music,
                direction,
                VolumeNotificationFlags.ShowUi);
        }
    }

    void AdjustBrightness(int delta)
    {
        // Android requires WRITE_SETTINGS permission for modifying system brightness.
        if (!Settings.System.CanWrite(accessibilityService)) return;

        var resolver = accessibilityService.ContentResolver;
        var current = Settings.System.GetInt(resolver, Settings.System.ScreenBrightness, 128);
        var next = Math.Max(0, Math.Min(255, current + delta));
        Settings.System.PutInt(resolver, Settings.System.ScreenBrightness, next);
    }

    void DispatchMediaKey(Keycode keyCode)
    {
        if (audioManager == null) return;

        var down = new KeyEvent(KeyEventActions.Down, keyCode);
        var up = new KeyEvent(KeyEventActions.Up, keyCode);

        audioManager.DispatchMediaKeyEvent(down);
        audioManager.DispatchMediaKeyEvent(up);
    }

    /// <summary>
    /// Screen pixel size for <see cref="GestureDescription"/> (full default display; matches gesture injection coords).
    /// </summary>
    void GetScreenSizePx(out int w, out int h)
    {
        w = 0;
        h = 0;
        try
        {
            var displayManager = accessibilityService.GetSystemService(Context.DisplayService) as DisplayManager;
            var display = displayManager?.GetDisplay(Display.DefaultDisplay);
            if (display != null)
            {
                var metrics = new DisplayMetrics();
#pragma warning disable CA1422
                display.GetRealMetrics(metrics);
#pragma warning restore CA1422
                w = metrics.WidthPixels;
                h = metrics.HeightPixels;
            }
        }
        catch (Exception ex)
        {
            Log.Warn("RingActionExecutor", "GetScreenSizePx: " + ex.Message);
        }
        if (w <= 0 || h <= 0)
        {
            var dm = accessibilityService.Resources?.DisplayMetrics;
            w = dm?.WidthPixels ?? 0;
            h = dm?.HeightPixels ?? 0;
        }
    }

    void DispatchGesturePath(Android.Graphics.Path path, long durationMs)
    {
        if (durationMs < 1) durationMs = 1;
        try
        {
            var stroke = new GestureDescription.StrokeDescription(path, 0, durationMs);
            var builder = new GestureDescription.Builder();
            builder.AddStroke(stroke);
            var gesture = builder.Build();
            if (gesture == null) return;
            var ok = accessibilityService.DispatchGesture(gesture, null, gestureHandler);
            if (!ok)
                Log.Warn("RingActionExecutor",
                    "DispatchGesture returned false — ensure canPerformGestures is enabled (turn accessibility service OFF then ON after updating the app)");
        }
        finally
        {
            path.Dispose();
        }
    }

    /// <summary> Tap near left/right screen edge (e-book style next/prev by edge tap). </summary>
    void DispatchEdgeTap(bool isLeft)
    {
        GetScreenSizePx(out var w, out var h);
        if (w <= 0 || h <= 0) return;

        var density = accessibilityService.Resources?.DisplayMetrics?.Density ?? 2f;
        var margin = Math.Max(48f * density, w * 0.035f);
        var x = isLeft ? margin : w - margin;
        var y = h * 0.5f;
        y = Math.Clamp(y, h * 0.12f, h * 0.88f);

        var path = new Android.Graphics.Path();
        path.MoveTo(x, y);
        path.LineTo(x + (isLeft ? 1f : -1f), y);
        DispatchGesturePath(path, EdgeTapStrokeMs);
    }

    /// <summary> Two quick taps at the same edge position (e.g. zoom / reader shortcuts). </summary>
    void DispatchEdgeDoubleTap(bool isLeft)
    {
        DispatchEdgeTap(isLeft);
        var runnable = new Java.Lang.Runnable(() => DispatchEdgeTap(isLeft));
        gestureHandler.PostDelayed(runnable, DoubleTapSecondStartDelayMs);
    }

    /// <summary>
    /// Horizontal swipe from screen center (finger moves left or right). Typical for in-app page turns.
    /// </summary>
    void DispatchCenterSwipe(bool isSwipeLeft)
    {
        GetScreenSizePx(out var w, out var h);
        if (w <= 0 || h <= 0) return;

        var cx = w * 0.5f;
        var y = h * 0.5f;
        y = Math.Clamp(y, h * 0.12f, h * 0.88f);

        var half = Math.Min(w * 0.45f, 420f) * 0.5f;
        float x1, x2;
        if (isSwipeLeft)
        {
            x1 = cx + half;
            x2 = cx - half;
        }
        else
        {
            x1 = cx - half;
            x2 = cx + half;
        }

        var path = new Android.Graphics.Path();
        path.MoveTo(x1, y);
        path.LineTo(x2, y);
        DispatchGesturePath(path, SwipeGestureDurationMs);
    }

    /// <summary>
    /// Vertical swipe from mid-screen (finger moves up or down). Up = toward smaller Y; common for list/web scroll.
    /// </summary>
    void DispatchCenterVerticalSwipe(bool swipeUp)
    {
        GetScreenSizePx(out var w, out var h);
        if (w <= 0 || h <= 0) return;

        var cx = w * 0.5f;
        var cy = h * 0.5f;
        cy = Math.Clamp(cy, h * 0.15f, h * 0.85f);

        var half = Math.Min(h * 0.38f, 560f) * 0.25f;
        float y1, y2;
        if (swipeUp)
        {
            y1 = cy + half;
            y2 = cy - half;
        }
        else
        {
            y1 = cy - half;
            y2 = cy + half;
        }

        var path = new Android.Graphics.Path();
        path.MoveTo(cx, y1);
        path.LineTo(cx, y2);
        DispatchGesturePath(path, SwipeGestureDurationMs);
    }

    /// <summary>
    /// Horizontal pinch at mid-screen (from center). <paramref name="pinchOut"/> true: start near center, move outward; false: start wide, move inward.
    /// </summary>
    void DispatchCenterPinch(bool pinchOut)
    {
        GetScreenSizePx(out var w, out var h);
        if (w <= 0 || h <= 0) return;

        var cx = w * 0.5f;
        var cy = h * 0.5f;
        cy = Math.Clamp(cy, h * 0.12f, h * 0.88f);

        var inner = Math.Min(w, h) * 0.12f;
        var outer = Math.Min(w, h) * 0.30f;
        inner = Math.Max(inner, 24f);
        outer = Math.Max(outer, inner + 48f);
        outer = Math.Min(outer, Math.Min(w, h) * 0.42f);

        float lStart, lEnd, rStart, rEnd;
        if (pinchOut)
        {
            lStart = cx - inner;
            lEnd = cx - outer;
            rStart = cx + inner;
            rEnd = cx + outer;
        }
        else
        {
            lStart = cx - outer;
            lEnd = cx - inner;
            rStart = cx + outer;
            rEnd = cx + inner;
        }

        lStart = Math.Clamp(lStart, 4f, w - 4f);
        lEnd = Math.Clamp(lEnd, 4f, w - 4f);
        rStart = Math.Clamp(rStart, 4f, w - 4f);
        rEnd = Math.Clamp(rEnd, 4f, w - 4f);

        var pathL = new Android.Graphics.Path();
        var pathR = new Android.Graphics.Path();
        pathL.MoveTo(lStart, cy);
        pathL.LineTo(lEnd, cy);
        pathR.MoveTo(rStart, cy);
        pathR.LineTo(rEnd, cy);

        try
        {
            var dur = PinchGestureDurationMs;
            if (dur < 1) dur = 1;
            var strokeL = new GestureDescription.StrokeDescription(pathL, 0, dur);
            var strokeR = new GestureDescription.StrokeDescription(pathR, 0, dur);
            var builder = new GestureDescription.Builder();
            builder.AddStroke(strokeL);
            builder.AddStroke(strokeR);
            var gesture = builder.Build();
            if (gesture == null) return;
            var ok = accessibilityService.DispatchGesture(gesture, null, gestureHandler);
            if (!ok)
                Log.Warn("RingActionExecutor",
                    "DispatchGesture (pinch) returned false — ensure canPerformGestures is enabled (turn accessibility service OFF then ON after updating the app)");
        }
        finally
        {
            pathL.Dispose();
            pathR.Dispose();
        }
    }
}

