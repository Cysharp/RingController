using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.OS;
using Android.Runtime;
using Android.Text;
using Android.Views;
using Android.Widget;
using System;
using System.Collections.Generic;
using System.Linq;

namespace RingController;

public sealed class RingSettingsPanelHost
{
    public RingSettingsPanelHost(Activity activity, Action<RingConfig> saveFullConfig)
    {
        this.activity = activity;
        this.saveFullConfig = saveFullConfig;
    }

    readonly Activity activity;
    readonly Action<RingConfig> saveFullConfig;
    RingConfig editedConfig = null!;

    readonly Handler persistHandler = new(Looper.MainLooper!);
    Java.Lang.Runnable? persistRunnable;
    bool suppressPersist;

    HorizontalScrollView? tabStripScrollRef;
    Action<MotionEvent?>? tabStripTouchDispatchHook;
    bool tabStripPointerDown;

    IntStepperUi? minAbsStepper;
    RadioGroup? radioGroup;
    IntStepperUi? accumTimeoutStepper;
    DefaultActionUi? leftDefault;
    DefaultActionUi? rightDefault;
    TextView? leftActionSummary;
    TextView? rightActionSummary;
    LinearLayout? seqContainer;
    List<SequenceUi>? allSequences;
    IntStepperUi? gestureSequenceTimeoutStepper;

    RingVisualizationCanvas? ringCanvas;
    ImageView? ringImage;
    Bitmap? ringBitmap;
    readonly Handler ringFadeHandler = new(Looper.MainLooper!);
    Java.Lang.Runnable? ringFadeRunnable;

    const int AccumulateTabRadioId = 1;
    const int ThresholdTabRadioId = 2;
    const int EachTabRadioId = 3;
    const int GestureTabRadioId = 4;

    float dp;

    static readonly (RingActionKind Kind, int LabelResId)[] ActionKindItems =
    {
        (RingActionKind.None, Resource.String.action_none),
        (RingActionKind.VolumeDown, Resource.String.action_volume_down),
        (RingActionKind.VolumeUp, Resource.String.action_volume_up),
        (RingActionKind.BrightnessDown, Resource.String.action_brightness_down),
        (RingActionKind.BrightnessUp, Resource.String.action_brightness_up),
        (RingActionKind.MediaPlayPause, Resource.String.action_media_play_pause),
        (RingActionKind.MediaStop, Resource.String.action_media_stop),
        (RingActionKind.MediaPrev, Resource.String.action_media_prev),
        (RingActionKind.MediaNext, Resource.String.action_media_next),
        (RingActionKind.MediaRewind, Resource.String.action_media_rewind),
        (RingActionKind.MediaFastForward, Resource.String.action_media_ff),
        (RingActionKind.LaunchApp, Resource.String.action_launch_app),
        (RingActionKind.BroadcastIntentAction, Resource.String.action_broadcast_intent),
        (RingActionKind.OpenUrl, Resource.String.action_open_url),
        (RingActionKind.Screenshot, Resource.String.action_screenshot),
        (RingActionKind.LockScreen, Resource.String.action_lock_screen),
        (RingActionKind.Flashlight, Resource.String.action_flashlight),
        (RingActionKind.RotationLock, Resource.String.action_rotation_lock),
        (RingActionKind.TapLeftEdge, Resource.String.action_tap_left),
        (RingActionKind.TapRightEdge, Resource.String.action_tap_right),
        (RingActionKind.DoubleTapLeftEdge, Resource.String.action_double_tap_left),
        (RingActionKind.DoubleTapRightEdge, Resource.String.action_double_tap_right),
        (RingActionKind.SwipeLeftFromCenter, Resource.String.action_swipe_left),
        (RingActionKind.SwipeRightFromCenter, Resource.String.action_swipe_right),
        (RingActionKind.SwipeUpFromCenter, Resource.String.action_swipe_up),
        (RingActionKind.SwipeDownFromCenter, Resource.String.action_swipe_down),
        (RingActionKind.PinchIn, Resource.String.action_pinch_in),
        (RingActionKind.PinchOut, Resource.String.action_pinch_out),
    };

    // ── Config UI model classes ──

    sealed class DefaultActionUi(Spinner kindSpinner, LinearLayout rowPkg, EditText launchPackageEdit, EditText intentActionEdit, EditText urlEdit)
    {
        public Spinner KindSpinner { get; } = kindSpinner;
        public LinearLayout RowPkg { get; } = rowPkg;
        public EditText LaunchPackageEdit { get; } = launchPackageEdit;
        public EditText IntentActionEdit { get; } = intentActionEdit;
        public EditText UrlEdit { get; } = urlEdit;
    }

    sealed class SequenceStepUi
    {
        public Spinner DirectionSpinner { get; set; } = null!;
        public IntStepperUi MinAbsStepper { get; set; } = null!;
        public LinearLayout Root { get; set; } = null!;
    }

    sealed class SequenceUi
    {
        public LinearLayout StepsContainer { get; set; } = null!;
        public List<SequenceStepUi> Steps { get; } = new();
        public Spinner ActionSpinner { get; set; } = null!;
        public EditText ActionPkgEdit { get; set; } = null!;
        public EditText ActionIntentEdit { get; set; } = null!;
        public EditText ActionUrlEdit { get; set; } = null!;
        public LinearLayout BlockRoot { get; set; } = null!;
    }

    /// <summary>
    /// SeekBar with discrete values (e.g. 1, 10, 20, …). Progress is the <b>index</b> into <see cref="Values"/>
    /// so each step has equal width on the bar (1→10 is as wide as 10→20). Not linear in numeric value.
    /// </summary>
    sealed class IntStepperUi
    {
        public SeekBar Seek { get; }
        public TextView ValueText { get; }
        public int[] Values { get; }

        public IntStepperUi(SeekBar seek, TextView valueText, int[] values)
        {
            Seek = seek;
            ValueText = valueText;
            Values = values;
        }

        public int Value
        {
            get
            {
                var idx = Seek.Progress;
                if (idx < 0 || idx >= Values.Length) idx = Math.Clamp(idx, 0, Math.Max(0, Values.Length - 1));
                return Values[idx];
            }
            set
            {
                var snapped = NearestDiscreteValue(Values, value);
                var idx = Array.IndexOf(Values, snapped);
                if (idx < 0) idx = 0;
                Seek.Progress = idx;
                ValueText.Text = snapped.ToString();
            }
        }
    }

    /// <summary> 1, then 10, 20, … up to maxV (aligned with 10-step UI). </summary>
    static int[] BuildDiscreteSliderValues(int minV, int maxV)
    {
        var list = new List<int>();
        if (maxV < minV) return [];
        if (minV <= 1 && maxV >= 1) list.Add(1);
        for (var v = 10; v <= maxV; v += 10)
            list.Add(v);
        return list.ToArray();
    }

    /// <summary> Closest allowed value; on tie pick the smaller (nearer to 1). </summary>
    static int NearestDiscreteValue(int[] values, int raw)
    {
        if (values.Length == 0) return raw;
        var best = values[0];
        var bestDiff = int.MaxValue;
        foreach (var v in values)
        {
            var d = Math.Abs(v - raw);
            if (d < bestDiff || (d == bestDiff && v < best))
            {
                bestDiff = d;
                best = v;
            }
        }
        return best;
    }

    // ── Theme helpers ──

    Color Clr(int colorRes) => new Color(activity.GetColor(colorRes));

    // ── Shared helpers ──

    Spinner MakeActionSpinner()
    {
        var labels = ActionKindItems.Select(i => activity.GetString(i.LabelResId)).ToList();
        var spinner = new Spinner(activity);
        var adapter = new ArrayAdapter<string>(activity, Android.Resource.Layout.SimpleSpinnerItem, labels);
        adapter.SetDropDownViewResource(Android.Resource.Layout.SimpleSpinnerDropDownItem);
        spinner.Adapter = adapter;
        return spinner;
    }

    static void SetSpinnerSelected(Spinner spinner, RingActionKind kind)
    {
        var idx = Array.FindIndex(ActionKindItems, i => i.Kind == kind);
        if (idx >= 0) spinner.SetSelection(idx);
    }

    static RingActionKind GetSpinnerSelectedKind(Spinner spinner)
    {
        var idx = spinner.SelectedItemPosition;
        if (idx < 0 || idx >= ActionKindItems.Length) return ActionKindItems[0].Kind;
        return ActionKindItems[idx].Kind;
    }

    string? GetAppLabelForPackage(string? pkg)
    {
        if (string.IsNullOrWhiteSpace(pkg)) return null;
        try
        {
            var pm = activity.PackageManager;
            if (pm == null) return null;
            var info = pm.GetApplicationInfo(pkg!, (PackageInfoFlags)0);
            return info.LoadLabel(pm)?.ToString();
        }
        catch
        {
            return null;
        }
    }

    string FormatCompactActionSummary(Spinner spinner, EditText pkgEdit, EditText intentEdit, EditText urlEdit)
    {
        var kind = GetSpinnerSelectedKind(spinner);
        if (kind == RingActionKind.LaunchApp)
        {
            var pkg = (pkgEdit.Text ?? "").Trim();
            if (string.IsNullOrEmpty(pkg))
                return activity.GetString(Resource.String.action_launch_app);
            var appLabel = GetAppLabelForPackage(pkg);
            var resolved = string.IsNullOrEmpty(appLabel) ? pkg : appLabel!;
            return (activity.GetString(Resource.String.ring_action_launch_summary) ?? "").Replace("%1$s", resolved);
        }
        if (kind == RingActionKind.BroadcastIntentAction)
        {
            var ia = (intentEdit.Text ?? "").Trim();
            if (string.IsNullOrEmpty(ia))
                return activity.GetString(Resource.String.action_broadcast_intent);
            return (activity.GetString(Resource.String.ring_action_broadcast_summary) ?? "").Replace("%1$s", ia);
        }
        if (kind == RingActionKind.OpenUrl)
        {
            var u = (urlEdit.Text ?? "").Trim();
            if (string.IsNullOrEmpty(u))
                return activity.GetString(Resource.String.action_open_url);
            return (activity.GetString(Resource.String.ring_action_url_summary) ?? "").Replace("%1$s", u);
        }
        var idx = spinner.SelectedItemPosition;
        if (idx >= 0 && idx < ActionKindItems.Length)
            return activity.GetString(ActionKindItems[idx].LabelResId);
        return "";
    }

    void SetupLaunchAppPickerRow(LinearLayout rowPkg, EditText hiddenPkgEdit, Action onPackagePicked)
    {
        rowPkg.RemoveAllViews();
        var btn = MakeOutlinedButton(activity.GetString(Resource.String.ring_app_pick_button));
        btn.Click += (_, _) =>
        {
            LauncherAppPickerHelper.Show(activity, p =>
            {
                if (p != null)
                {
                    hiddenPkgEdit.Text = p;
                    onPackagePicked();
                }
            });
        };
        rowPkg.AddView(btn);
    }

    static void WireLaunchPackageVisibility(Spinner spinner, LinearLayout rowPkg)
    {
        void Update()
        {
            var kind = GetSpinnerSelectedKind(spinner);
            rowPkg.Visibility = kind == RingActionKind.LaunchApp ? ViewStates.Visible : ViewStates.Gone;
        }
        spinner.ItemSelected += (_, _) => Update();
        Update();
    }

    void ShowBroadcastActionDialog(EditText intentEdit, Action sync)
    {
        var edit = new EditText(activity) { Text = intentEdit.Text ?? "" };
        edit.Hint = activity.GetString(Resource.String.ring_broadcast_action_hint);
        edit.InputType = InputTypes.ClassText;
        edit.SetSingleLine(false);
        var padH = (int)(24 * dp);
        var padV = (int)(12 * dp);
        edit.SetPadding(padH, padV, padH, padV);
        var dlg = new Android.App.AlertDialog.Builder(activity)
            .SetTitle(Resource.String.ring_broadcast_action_title)
            .SetView(edit)
            .SetPositiveButton(Resource.String.save, (_, _) =>
            {
                intentEdit.Text = edit.Text?.ToString() ?? "";
                sync();
            })
            .SetNegativeButton(Resource.String.cancel, (_, _) => { })
            .Create();
        dlg.Show();
        dlg.GetButton((int)DialogButtonType.Positive)?.SetAllCaps(false);
        dlg.GetButton((int)DialogButtonType.Negative)?.SetAllCaps(false);
    }

    void ShowUrlInputDialog(EditText targetEdit, Action sync)
    {
        var edit = new EditText(activity) { Text = targetEdit.Text ?? "" };
        edit.Hint = activity.GetString(Resource.String.ring_url_hint);
        edit.InputType = InputTypes.ClassText | InputTypes.TextVariationUri;
        edit.SetSingleLine(true);
        var padH = (int)(24 * dp);
        var padV = (int)(12 * dp);
        edit.SetPadding(padH, padV, padH, padV);
        var dlg = new Android.App.AlertDialog.Builder(activity)
            .SetTitle(Resource.String.ring_url_dialog_title)
            .SetView(edit)
            .SetPositiveButton(Resource.String.save, (_, _) =>
            {
                targetEdit.Text = edit.Text?.ToString() ?? "";
                sync();
            })
            .SetNegativeButton(Resource.String.cancel, (_, _) => { })
            .Create();
        dlg.Show();
        dlg.GetButton((int)DialogButtonType.Positive)?.SetAllCaps(false);
        dlg.GetButton((int)DialogButtonType.Negative)?.SetAllCaps(false);
    }

    TextView MakeLabel(int stringId)
    {
        var tv = new TextView(activity) { Text = activity.GetString(stringId), TextSize = 12f };
        tv.SetTextColor(Clr(Resource.Color.md_theme_onSurfaceVariant));
        return tv;
    }

    EditText MakeNumberEdit(string hint, int? initial)
    {
        var edit = new EditText(activity) { Hint = hint };
        edit.InputType = InputTypes.ClassNumber | InputTypes.NumberFlagSigned;
        edit.SetSingleLine(true);
        edit.Text = initial?.ToString() ?? "";
        edit.SetTextColor(Clr(Resource.Color.md_theme_onSurface));
        edit.SetHintTextColor(Clr(Resource.Color.md_theme_onSurfaceVariant));
        return edit;
    }

    EditText MakeLongEdit(string hint, long? initial)
    {
        var edit = new EditText(activity) { Hint = hint };
        edit.InputType = InputTypes.ClassNumber;
        edit.SetSingleLine(true);
        edit.Text = initial?.ToString() ?? "";
        edit.SetTextColor(Clr(Resource.Color.md_theme_onSurface));
        edit.SetHintTextColor(Clr(Resource.Color.md_theme_onSurfaceVariant));
        return edit;
    }

    EditText MakeTextEdit(string hint, string? initial)
    {
        var edit = new EditText(activity) { Hint = hint };
        edit.InputType = InputTypes.ClassText;
        edit.SetSingleLine(true);
        edit.Text = initial ?? "";
        edit.SetTextColor(Clr(Resource.Color.md_theme_onSurface));
        edit.SetHintTextColor(Clr(Resource.Color.md_theme_onSurfaceVariant));
        return edit;
    }

    static RingActionConfig BuildActionConfig(Spinner spinner, EditText pkgEdit, EditText intentEdit, EditText urlEdit)
    {
        var kind = GetSpinnerSelectedKind(spinner);
        var cfg = new RingActionConfig { Kind = kind };
        if (kind == RingActionKind.LaunchApp)
            cfg.LaunchPackageName = (pkgEdit.Text ?? "").Trim();
        else if (kind == RingActionKind.BroadcastIntentAction)
            cfg.IntentAction = (intentEdit.Text ?? "").Trim();
        else if (kind == RingActionKind.OpenUrl)
            cfg.UrlString = (urlEdit.Text ?? "").Trim();
        return cfg;
    }

    static RingActionConfig BuildActionConfig(DefaultActionUi ui) =>
        BuildActionConfig(ui.KindSpinner, ui.LaunchPackageEdit, ui.IntentActionEdit, ui.UrlEdit);

    static int ParseInt(string? text, int def) => int.TryParse(text, out var v) ? v : def;
    static long ParseLong(string? text, long def) => long.TryParse(text, out var v) ? v : def;

    static RingDirectionMappingConfig MapFromUi(DefaultActionUi defUi) =>
        new() { DefaultAction = BuildActionConfig(defUi) };

    static RingExecutionMode RadioIdToMode(int radioId) => radioId switch
    {
        AccumulateTabRadioId => RingExecutionMode.ThresholdAccumulatedRepeat,
        ThresholdTabRadioId => RingExecutionMode.ThresholdCrossing,
        EachTabRadioId => RingExecutionMode.EachEvent,
        GestureTabRadioId => RingExecutionMode.Gesture,
        _ => RingExecutionMode.EachEvent,
    };

    static int ModeToRadioId(RingExecutionMode mode) => mode switch
    {
        RingExecutionMode.ThresholdAccumulatedRepeat => AccumulateTabRadioId,
        RingExecutionMode.ThresholdCrossing => ThresholdTabRadioId,
        RingExecutionMode.EachEvent => EachTabRadioId,
        RingExecutionMode.Gesture => GestureTabRadioId,
        _ => EachTabRadioId,
    };

    static RingModeProfile ProfileForRadio(RingConfig cfg, int radioId) => radioId switch
    {
        AccumulateTabRadioId => cfg.AccumulateMode,
        ThresholdTabRadioId => cfg.ThresholdMode,
        EachTabRadioId => cfg.EachMode,
        GestureTabRadioId => cfg.GestureMode,
        _ => cfg.EachMode,
    };

    void SaveUiToProfile(RingConfig cfg, int radioId)
    {
        var p = ProfileForRadio(cfg, radioId);
        p.MinAbsToTrigger = minAbsStepper!.Value;
        p.AccumulationTimeoutMs = accumTimeoutStepper!.Value;
        p.GestureSequenceTimeoutMs = gestureSequenceTimeoutStepper!.Value;
        p.Normal = new RingContextConfig
        {
            Left = MapFromUi(leftDefault!),
            Right = MapFromUi(rightDefault!),
            Sequences = CollectSequences(allSequences!),
        };
    }

    void SyncCompactActionSummary(TextView summary, Spinner spinner, EditText pkgEdit, EditText intentEdit, EditText urlEdit)
    {
        summary.Text = FormatCompactActionSummary(spinner, pkgEdit, intentEdit, urlEdit);
    }

    void ApplyCompactMappingToUi(DefaultActionUi ui, RingDirectionMappingConfig mapping, TextView summary)
    {
        SetSpinnerSelected(ui.KindSpinner, mapping.DefaultAction.Kind);
        ui.LaunchPackageEdit.Text = mapping.DefaultAction.LaunchPackageName ?? "";
        ui.IntentActionEdit.Text = mapping.DefaultAction.IntentAction ?? "";
        ui.UrlEdit.Text = mapping.DefaultAction.UrlString ?? "";
        SyncCompactActionSummary(summary, ui.KindSpinner, ui.LaunchPackageEdit, ui.IntentActionEdit, ui.UrlEdit);
        ui.RowPkg.Visibility = ViewStates.Gone;
    }

    void RebuildSequencesFromProfile(RingModeProfile profile)
    {
        if (seqContainer == null || allSequences == null) return;
        seqContainer.RemoveAllViews();
        allSequences.Clear();
        for (int i = 0; i < profile.Normal.Sequences.Count; i++)
        {
            var su = BuildSequenceUi(profile.Normal.Sequences[i], seqContainer, allSequences);
            var suLp = new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MatchParent, LinearLayout.LayoutParams.WrapContent);
            suLp.BottomMargin = (int)(16 * dp);
            seqContainer.AddView(su.BlockRoot, suLp);
            allSequences.Add(su);
        }
    }

    void LoadProfileIntoUi(RingConfig cfg, int radioId)
    {
        var p = ProfileForRadio(cfg, radioId);
        minAbsStepper!.Value = p.MinAbsToTrigger;
        accumTimeoutStepper!.Value = (int)Math.Clamp(p.AccumulationTimeoutMs, 1, 999);
        gestureSequenceTimeoutStepper!.Value = (int)Math.Clamp(p.GestureSequenceTimeoutMs, 1, 2000);
        ApplyCompactMappingToUi(leftDefault!, p.Normal.Left, leftActionSummary!);
        ApplyCompactMappingToUi(rightDefault!, p.Normal.Right, rightActionSummary!);
        RebuildSequencesFromProfile(p);
    }

    List<RingSequenceRuleConfig> CollectSequences(List<SequenceUi> seqUis)
    {
        var list = new List<RingSequenceRuleConfig>();
        foreach (var su in seqUis)
        {
            if (su.Steps.Count < 1) continue;
            var steps = su.Steps.Select(s => new RingSequenceStepConfig
            {
                Direction = s.DirectionSpinner.SelectedItemPosition == 0 ? RingDirection.Left : RingDirection.Right,
                MinAbs = s.MinAbsStepper.Value,
            }).ToList();
            var action = BuildActionConfig(su.ActionSpinner, su.ActionPkgEdit, su.ActionIntentEdit, su.ActionUrlEdit);
            if (action.Kind == RingActionKind.None) continue;
            var timeoutMs = (long)gestureSequenceTimeoutStepper!.Value;
            list.Add(new RingSequenceRuleConfig
            {
                Steps = steps,
                // Timeout slider is for sensor-idle debounce; whole-gesture cap is a different use case, so disabled (0).
                MaxGapMs = timeoutMs,
                MaxTotalMs = 0,
                Action = action,
            });
        }
        return list;
    }

    void SchedulePersistDelayed()
    {
        if (suppressPersist) return;
        persistHandler.RemoveCallbacks(persistRunnable!);
        persistRunnable = new Java.Lang.Runnable(PersistRingConfig);
        persistHandler.PostDelayed(persistRunnable, 400);
    }

    void WirePersistOnEdit(EditText e)
    {
        e.TextChanged += (_, _) => SchedulePersistDelayed();
    }

    void WirePersistOnIntStepper(IntStepperUi s)
    {
        void OnChanged() => SchedulePersistDelayed();
        s.Seek.ProgressChanged += (_, e) =>
        {
            var idx = Math.Clamp(e.Progress, 0, Math.Max(0, s.Values.Length - 1));
            var snapped = s.Values[idx];
            s.ValueText.Text = snapped.ToString();
            if (e.FromUser) OnChanged();
        };
    }

    IntStepperUi AddIntStepperSettingRow(LinearLayout parent, int labelRes, int minV, int maxV, int initial, int topMarginDp)
    {
        var row = new LinearLayout(activity) { Orientation = Orientation.Horizontal };
        row.SetGravity(GravityFlags.CenterVertical);
        row.SetPadding(0, (int)(10 * dp), 0, (int)(10 * dp));
        var rowLp = new LinearLayout.LayoutParams(
            LinearLayout.LayoutParams.MatchParent, LinearLayout.LayoutParams.WrapContent);
        if (topMarginDp > 0)
            rowLp.TopMargin = (int)(topMarginDp * dp);
        parent.AddView(row, rowLp);

        var label = new TextView(activity) { Text = activity.GetString(labelRes), TextSize = 14f };
        label.SetTextColor(Clr(Resource.Color.md_theme_onSurface));
        label.SetSingleLine(true);
        label.Ellipsize = TextUtils.TruncateAt.End;
        var labelLp = new LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WrapContent, 1f);
        labelLp.RightMargin = (int)(4 * dp);
        row.AddView(label, labelLp);

        var values = BuildDiscreteSliderValues(minV, maxV);
        var seek = new SeekBar(activity);
        seek.Max = Math.Max(0, values.Length - 1);
        var initSnapped = NearestDiscreteValue(values, Math.Clamp(initial, minV, maxV));
        seek.Progress = Math.Max(0, Array.IndexOf(values, initSnapped));
        var primary = Clr(Resource.Color.md_theme_primary);
        seek.ProgressTintList = Android.Content.Res.ColorStateList.ValueOf(primary);
        seek.ThumbTintList = Android.Content.Res.ColorStateList.ValueOf(primary);
        var seekLp = new LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WrapContent, 1.25f);
        seekLp.LeftMargin = (int)(2 * dp);
        seekLp.RightMargin = (int)(4 * dp);
        row.AddView(seek, seekLp);

        var valueText = new TextView(activity) { TextSize = 15f, Gravity = GravityFlags.CenterVertical };
        valueText.SetTextColor(Clr(Resource.Color.md_theme_onSurface));
        valueText.SetMinWidth((int)(44 * dp));
        valueText.Text = initSnapped.ToString();
        var valLp = new LinearLayout.LayoutParams(
            LinearLayout.LayoutParams.WrapContent, LinearLayout.LayoutParams.WrapContent);
        row.AddView(valueText, valLp);

        return new IntStepperUi(seek, valueText, values);
    }

    /// <summary> Discrete 1–maxV seek + value label for embedding in a horizontal row (e.g. gesture steps). </summary>
    IntStepperUi AttachHorizontalIntStepper(LinearLayout row, int minV, int maxV, int initial, float weight)
    {
        var values = BuildDiscreteSliderValues(minV, maxV);
        var seek = new SeekBar(activity);
        seek.Max = Math.Max(0, values.Length - 1);
        var initSnapped = NearestDiscreteValue(values, Math.Clamp(initial, minV, maxV));
        seek.Progress = Math.Max(0, Array.IndexOf(values, initSnapped));
        var primary = Clr(Resource.Color.md_theme_primary);
        seek.ProgressTintList = Android.Content.Res.ColorStateList.ValueOf(primary);
        seek.ThumbTintList = Android.Content.Res.ColorStateList.ValueOf(primary);
        var seekLp = new LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WrapContent, weight);
        seekLp.RightMargin = (int)(4 * dp);
        row.AddView(seek, seekLp);

        var valueText = new TextView(activity) { TextSize = 15f, Gravity = GravityFlags.CenterVertical };
        valueText.SetTextColor(Clr(Resource.Color.md_theme_onSurface));
        valueText.SetMinWidth((int)(44 * dp));
        valueText.Text = initSnapped.ToString();
        row.AddView(valueText, new LinearLayout.LayoutParams(
            LinearLayout.LayoutParams.WrapContent, LinearLayout.LayoutParams.WrapContent));

        return new IntStepperUi(seek, valueText, values);
    }

    void WirePersistDefaultAction(DefaultActionUi ui)
    {
        ui.KindSpinner.ItemSelected += (_, _) => PersistRingConfig();
        WirePersistOnEdit(ui.LaunchPackageEdit);
        WirePersistOnEdit(ui.IntentActionEdit);
        WirePersistOnEdit(ui.UrlEdit);
    }

    void WirePersistSequenceStep(SequenceStepUi step)
    {
        step.DirectionSpinner.ItemSelected += (_, _) => PersistRingConfig();
        WirePersistOnIntStepper(step.MinAbsStepper);
    }

    void WirePersistSequence(SequenceUi su)
    {
        WirePersistOnEdit(su.ActionPkgEdit);
        WirePersistOnEdit(su.ActionIntentEdit);
        WirePersistOnEdit(su.ActionUrlEdit);
        foreach (var st in su.Steps)
            WirePersistSequenceStep(st);
    }

    void PersistRingConfig()
    {
        if (suppressPersist) return;
        if (minAbsStepper == null || radioGroup == null || accumTimeoutStepper == null
            || gestureSequenceTimeoutStepper == null
            || leftDefault == null || rightDefault == null
            || allSequences == null)
            return;

        var rid = radioGroup.CheckedRadioButtonId;
        SaveUiToProfile(editedConfig, rid);
        editedConfig.ExecutionMode = rid == GestureTabRadioId
            ? RingExecutionMode.Gesture
            : RadioIdToMode(rid);
        saveFullConfig(editedConfig);
    }

    // ── Widget builders ──

    Button MakeOutlinedButton(string text)
    {
        var btn = new Button(activity) { Text = text };
        btn.SetAllCaps(false);
        btn.SetBackgroundResource(Resource.Drawable.button_outlined);
        btn.SetTextColor(Clr(Resource.Color.md_theme_primary));
        return btn;
    }

    Button MakeTextButton(string text, bool error = false)
    {
        var btn = new Button(activity) { Text = text };
        btn.SetAllCaps(false);
        var ta = activity.ObtainStyledAttributes(new[] { Android.Resource.Attribute.SelectableItemBackgroundBorderless });
        btn.Background = ta.GetDrawable(0);
        ta.Recycle();
        btn.SetTextColor(Clr(error ? Resource.Color.md_theme_error : Resource.Color.md_theme_primary));
        btn.SetPadding((int)(12 * dp), 0, (int)(12 * dp), 0);
        return btn;
    }

    ImageButton MakeCircleIconStepButton(int drawableRes, string contentDescription)
    {
        var btn = new ImageButton(activity);
        btn.SetImageResource(drawableRes);
        btn.ContentDescription = contentDescription;
        btn.ImageTintList = Android.Content.Res.ColorStateList.ValueOf(Clr(Resource.Color.md_theme_primary));
        var ta = activity.ObtainStyledAttributes(new[] { Android.Resource.Attribute.SelectableItemBackgroundBorderless });
        btn.Background = ta.GetDrawable(0);
        ta.Recycle();
        var side = (int)(40 * dp);
        btn.SetMinimumWidth(side);
        btn.SetMinimumHeight(side);
        btn.SetPadding((int)(8 * dp), (int)(8 * dp), (int)(8 * dp), (int)(8 * dp));
        return btn;
    }

    /// <summary> Same tap target as step ±; error tint (matches per-app row delete). </summary>
    ImageButton MakeDeleteGestureIconButton(string contentDescription)
    {
        var btn = new ImageButton(activity);
        btn.SetImageResource(Resource.Drawable.ic_delete);
        btn.ContentDescription = contentDescription;
        btn.SetColorFilter(Clr(Resource.Color.md_theme_error), PorterDuff.Mode.SrcIn!);
        var ta = activity.ObtainStyledAttributes(new[] { Android.Resource.Attribute.SelectableItemBackgroundBorderless });
        btn.Background = ta.GetDrawable(0);
        ta.Recycle();
        var side = (int)(40 * dp);
        btn.SetMinimumWidth(side);
        btn.SetMinimumHeight(side);
        btn.SetPadding((int)(8 * dp), (int)(8 * dp), (int)(8 * dp), (int)(8 * dp));
        return btn;
    }

    TextView CreateSectionHeader(int stringId)
    {
        var tv = new TextView(activity) { Text = activity.GetString(stringId), TextSize = 13f };
        tv.SetTextColor(Clr(Resource.Color.md_theme_primary));
        return tv;
    }

    View MakeDivider()
    {
        var div = new View(activity);
        div.SetBackgroundColor(Clr(Resource.Color.md_theme_outlineVariant));
        return div;
    }

    LinearLayout.LayoutParams DividerLp()
    {
        var lp = new LinearLayout.LayoutParams(LinearLayout.LayoutParams.MatchParent, (int)Math.Max(1, dp));
        lp.TopMargin = (int)(16 * dp);
        lp.BottomMargin = (int)(16 * dp);
        return lp;
    }

    // ── Direction block ──

    DefaultActionUi BuildDefaultActionBlock(RingDirectionMappingConfig mapping)
    {
        var wrap = new LinearLayout(activity) { Orientation = Orientation.Vertical };
        wrap.AddView(MakeLabel(Resource.String.ring_action_what));
        var spinner = MakeActionSpinner();
        var spinLp = new LinearLayout.LayoutParams(
            LinearLayout.LayoutParams.MatchParent, LinearLayout.LayoutParams.WrapContent);
        spinLp.TopMargin = (int)(2 * dp);
        wrap.AddView(spinner, spinLp);

        var rowPkg = new LinearLayout(activity) { Orientation = Orientation.Vertical };
        var pkgLblLp = new LinearLayout.LayoutParams(
            LinearLayout.LayoutParams.MatchParent, LinearLayout.LayoutParams.WrapContent);
        pkgLblLp.TopMargin = (int)(12 * dp);
        rowPkg.AddView(MakeLabel(Resource.String.ring_app_package_label), pkgLblLp);

        var pkgEdit = new EditText(activity) { Visibility = ViewStates.Gone };
        pkgEdit.InputType = InputTypes.ClassText;
        pkgEdit.SetSingleLine(true);
        pkgEdit.Text = mapping.DefaultAction.LaunchPackageName ?? "";

        var intentEdit = new EditText(activity) { Visibility = ViewStates.Gone };
        intentEdit.InputType = InputTypes.ClassText;
        intentEdit.SetSingleLine(true);
        intentEdit.Text = mapping.DefaultAction.IntentAction ?? "";

        var urlEdit = new EditText(activity) { Visibility = ViewStates.Gone };
        urlEdit.InputType = InputTypes.ClassText;
        urlEdit.SetSingleLine(true);
        urlEdit.Text = mapping.DefaultAction.UrlString ?? "";

        SetupLaunchAppPickerRow(rowPkg, pkgEdit, PersistRingConfig);

        wrap.AddView(rowPkg);

        var ui = new DefaultActionUi(spinner, rowPkg, pkgEdit, intentEdit, urlEdit);
        WireLaunchPackageVisibility(spinner, rowPkg);
        SetSpinnerSelected(spinner, mapping.DefaultAction.Kind);
        pkgEdit.Text = mapping.DefaultAction.LaunchPackageName ?? "";
        intentEdit.Text = mapping.DefaultAction.IntentAction ?? "";
        urlEdit.Text = mapping.DefaultAction.UrlString ?? "";
        WirePersistDefaultAction(ui);
        return ui;
    }

    void AddDirectionBlock(LinearLayout container, int titleRes, RingDirectionMappingConfig mapping,
        out DefaultActionUi d)
    {
        container.AddView(CreateSectionHeader(titleRes));
        d = BuildDefaultActionBlock(mapping);
        var inner = (LinearLayout)d.KindSpinner.Parent!;
        var innerLp = new LinearLayout.LayoutParams(
            LinearLayout.LayoutParams.MatchParent, LinearLayout.LayoutParams.WrapContent);
        innerLp.TopMargin = (int)(4 * dp);
        container.AddView(inner, innerLp);
        container.AddView(MakeDivider(), DividerLp());
    }

    // ── Compact direction row (settings-list style) ──

    void AddCompactDirectionRow(LinearLayout container, int labelRes,
        RingDirectionMappingConfig mapping, out DefaultActionUi d, out TextView actionSummaryText)
    {
        var row = new LinearLayout(activity) { Orientation = Orientation.Horizontal };
        row.SetGravity(GravityFlags.CenterVertical);
        row.SetPadding(0, (int)(14 * dp), 0, (int)(14 * dp));

        var label = new TextView(activity) { Text = activity.GetString(labelRes), TextSize = 15f };
        label.SetTextColor(Clr(Resource.Color.md_theme_onSurface));
        row.AddView(label, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));

        var valueHit = new LinearLayout(activity) { Orientation = Orientation.Horizontal };
        valueHit.SetGravity(GravityFlags.CenterVertical);
        valueHit.Clickable = true;
        valueHit.Focusable = true;
        var ta = activity.ObtainStyledAttributes(new[] { Android.Resource.Attribute.SelectableItemBackground });
        valueHit.Foreground = ta.GetDrawable(0);
        ta.Recycle();

        var valueText = new TextView(activity) { TextSize = 15f };
        valueText.SetTextColor(Clr(Resource.Color.md_theme_onSurfaceVariant));
        valueHit.AddView(valueText);

        var chevron = new TextView(activity) { Text = "  \u203A", TextSize = 18f };
        chevron.SetTextColor(Clr(Resource.Color.md_theme_outlineVariant));
        valueHit.AddView(chevron);

        row.AddView(valueHit, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));

        container.AddView(row);

        var spinner = MakeActionSpinner();
        spinner.Visibility = ViewStates.Gone;
        container.AddView(spinner);

        var rowPkg = new LinearLayout(activity) { Orientation = Orientation.Vertical, Visibility = ViewStates.Gone };
        var pkgEdit = new EditText(activity) { Visibility = ViewStates.Gone };
        pkgEdit.InputType = InputTypes.ClassText;
        pkgEdit.SetSingleLine(true);
        pkgEdit.Text = mapping.DefaultAction.LaunchPackageName ?? "";

        var intentEdit = new EditText(activity) { Visibility = ViewStates.Gone };
        intentEdit.InputType = InputTypes.ClassText;
        intentEdit.SetSingleLine(true);
        intentEdit.Text = mapping.DefaultAction.IntentAction ?? "";

        var urlEdit = new EditText(activity) { Visibility = ViewStates.Gone };
        urlEdit.InputType = InputTypes.ClassText;
        urlEdit.SetSingleLine(true);
        urlEdit.Text = mapping.DefaultAction.UrlString ?? "";

        void SyncValueText()
        {
            valueText.Text = FormatCompactActionSummary(spinner, pkgEdit, intentEdit, urlEdit);
        }

        d = new DefaultActionUi(spinner, rowPkg, pkgEdit, intentEdit, urlEdit);
        actionSummaryText = valueText;
        SetSpinnerSelected(spinner, mapping.DefaultAction.Kind);
        pkgEdit.Text = mapping.DefaultAction.LaunchPackageName ?? "";
        intentEdit.Text = mapping.DefaultAction.IntentAction ?? "";
        urlEdit.Text = mapping.DefaultAction.UrlString ?? "";

        spinner.ItemSelected += (_, _) =>
        {
            SyncValueText();
            PersistRingConfig();
        };
        SyncValueText();

        valueHit.Click += (_, _) =>
        {
            var labels = ActionKindItems.Select(i => activity.GetString(i.LabelResId)).ToArray();
            var builder = new Android.App.AlertDialog.Builder(activity)
                .SetItems(labels, (_, e) =>
                {
                    spinner.SetSelection(e.Which);
                    var kind = GetSpinnerSelectedKind(spinner);
                    if (kind != RingActionKind.LaunchApp)
                        pkgEdit.Text = "";
                    if (kind != RingActionKind.BroadcastIntentAction)
                        intentEdit.Text = "";
                    if (kind != RingActionKind.OpenUrl)
                        urlEdit.Text = "";
                    SyncValueText();
                    PersistRingConfig();
                    if (kind == RingActionKind.LaunchApp)
                    {
                        valueHit.Post(() => LauncherAppPickerHelper.Show(activity, p =>
                        {
                            if (p != null)
                            {
                                pkgEdit.Text = p;
                                SyncValueText();
                                PersistRingConfig();
                            }
                        }));
                    }
                    else if (kind == RingActionKind.BroadcastIntentAction)
                    {
                        valueHit.Post(() => ShowBroadcastActionDialog(intentEdit, () =>
                        {
                            SyncValueText();
                            PersistRingConfig();
                        }));
                    }
                    else if (kind == RingActionKind.OpenUrl)
                    {
                        valueHit.Post(() => ShowUrlInputDialog(urlEdit, () =>
                        {
                            SyncValueText();
                            PersistRingConfig();
                        }));
                    }
                })
                .SetNegativeButton(Resource.String.cancel, (_, _) => { });
            var dlg = builder.Create();
            dlg.Show();
            dlg.GetButton((int)DialogButtonType.Negative)?.SetAllCaps(false);
        };

        WirePersistOnEdit(pkgEdit);
        WirePersistOnEdit(intentEdit);
        WirePersistOnEdit(urlEdit);
    }

    // ── Sequence UI ──

    Spinner MakeDirectionSpinner()
    {
        var labels = new[] {
            activity.GetString(Resource.String.ring_direction_left_label),
            activity.GetString(Resource.String.ring_direction_right_label)
        };
        var spinner = new Spinner(activity);
        var adapter = new ArrayAdapter<string>(activity, Android.Resource.Layout.SimpleSpinnerItem, labels);
        adapter.SetDropDownViewResource(Android.Resource.Layout.SimpleSpinnerDropDownItem);
        spinner.Adapter = adapter;
        return spinner;
    }

    SequenceStepUi BuildSequenceStepUi(RingSequenceStepConfig? step)
    {
        var row = new LinearLayout(activity) { Orientation = Orientation.Horizontal };
        row.SetGravity(GravityFlags.CenterVertical);
        row.SetPadding(0, (int)(14 * dp), 0, (int)(14 * dp));

        var dirSpinner = MakeDirectionSpinner();
        dirSpinner.SetMinimumWidth((int)(100 * dp));
        var dirLp = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 0.52f);
        dirLp.RightMargin = (int)(8 * dp);
        row.AddView(dirSpinner, dirLp);

        var minInit = Math.Clamp(step?.MinAbs ?? 200, 1, 2000);
        var minStepper = AttachHorizontalIntStepper(row, 10, 2000, minInit, 1f);

        if (step != null) dirSpinner.SetSelection(step.Direction == RingDirection.Left ? 0 : 1);
        return new SequenceStepUi { DirectionSpinner = dirSpinner, MinAbsStepper = minStepper, Root = row };
    }

    SequenceUi BuildSequenceUi(RingSequenceRuleConfig? seq,
        LinearLayout parentContainer, List<SequenceUi> allSequences)
    {
        var inner = new LinearLayout(activity) { Orientation = Orientation.Vertical };

        var actRow = new LinearLayout(activity) { Orientation = Orientation.Horizontal };
        actRow.SetGravity(GravityFlags.CenterVertical);
        actRow.SetPadding(0, (int)(4 * dp), 0, (int)(10 * dp));
        inner.AddView(actRow);

        var actLabel = new TextView(activity) { Text = activity.GetString(Resource.String.ring_sequence_action), TextSize = 15f };
        actLabel.SetTextColor(Clr(Resource.Color.md_theme_onSurface));
        actRow.AddView(actLabel, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));

        var actValueHit = new LinearLayout(activity) { Orientation = Orientation.Horizontal };
        actValueHit.SetGravity(GravityFlags.CenterVertical);
        actValueHit.Clickable = true;
        actValueHit.Focusable = true;
        var actTa = activity.ObtainStyledAttributes(new[] { Android.Resource.Attribute.SelectableItemBackground });
        actValueHit.Foreground = actTa.GetDrawable(0);
        actTa.Recycle();

        var actValueText = new TextView(activity) { TextSize = 15f };
        actValueText.SetTextColor(Clr(Resource.Color.md_theme_onSurfaceVariant));
        actValueHit.AddView(actValueText);

        var actChevron = new TextView(activity) { Text = "  \u203A", TextSize = 18f };
        actChevron.SetTextColor(Clr(Resource.Color.md_theme_outlineVariant));
        actValueHit.AddView(actChevron);

        actRow.AddView(actValueHit, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));

        var stepsContainer = new LinearLayout(activity) { Orientation = Orientation.Vertical };
        var stepsLp = new LinearLayout.LayoutParams(
            LinearLayout.LayoutParams.MatchParent, LinearLayout.LayoutParams.WrapContent);
        stepsLp.TopMargin = (int)(4 * dp);
        inner.AddView(stepsContainer, stepsLp);

        var seqUi = new SequenceUi
        {
            StepsContainer = stepsContainer, BlockRoot = inner,
            ActionSpinner = null!,
            ActionPkgEdit = null!,
            ActionIntentEdit = null!,
            ActionUrlEdit = null!,
        };

        var steps = seq?.Steps is { Count: > 0 } nonEmpty
            ? nonEmpty
            : new List<RingSequenceStepConfig>(RingConfig.CreateDefaultGestureSequenceRule().Steps);
        for (int i = 0; i < steps.Count; i++)
        {
            var stepUi = BuildSequenceStepUi(steps[i]);
            stepsContainer.AddView(stepUi.Root);
            seqUi.Steps.Add(stepUi);
        }

        var stepBtnRow = new LinearLayout(activity) { Orientation = Orientation.Horizontal };
        stepBtnRow.SetGravity(GravityFlags.CenterVertical | GravityFlags.End);
        var removeStepBtn = MakeCircleIconStepButton(
            Resource.Drawable.ic_ring_step_remove,
            activity.GetString(Resource.String.ring_sequence_step_remove_cd));
        var addStepBtn = MakeCircleIconStepButton(
            Resource.Drawable.ic_ring_step_add,
            activity.GetString(Resource.String.ring_sequence_step_add_cd));
        var removeGestureBtn = MakeDeleteGestureIconButton(
            activity.GetString(Resource.String.ring_sequence_remove_gesture_cd));
        var removeStepLp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent);
        var addStepLp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent);
        addStepLp.LeftMargin = (int)(4 * dp);
        var removeGestureLp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent);
        removeGestureLp.LeftMargin = (int)(12 * dp);
        stepBtnRow.AddView(removeStepBtn, removeStepLp);
        stepBtnRow.AddView(addStepBtn, addStepLp);
        stepBtnRow.AddView(removeGestureBtn, removeGestureLp);
        var stepBtnLp = new LinearLayout.LayoutParams(
            LinearLayout.LayoutParams.MatchParent, LinearLayout.LayoutParams.WrapContent);
        stepBtnLp.TopMargin = (int)(8 * dp);
        inner.AddView(stepBtnRow, stepBtnLp);

        addStepBtn.Click += (_, _) =>
        {
            var ns = BuildSequenceStepUi(null);
            seqUi.StepsContainer.AddView(ns.Root);
            seqUi.Steps.Add(ns);
            WirePersistSequenceStep(ns);
            PersistRingConfig();
        };
        removeStepBtn.Click += (_, _) =>
        {
            if (seqUi.Steps.Count <= 1) return;
            seqUi.StepsContainer.RemoveView(seqUi.Steps[^1].Root);
            seqUi.Steps.RemoveAt(seqUi.Steps.Count - 1);
            PersistRingConfig();
        };

        removeGestureBtn.Click += (_, _) =>
        {
            var dlg = new Android.App.AlertDialog.Builder(activity)
                .SetMessage(Resource.String.ring_sequence_remove_gesture_confirm)
                .SetPositiveButton(Resource.String.remove, (_, _) =>
                {
                    parentContainer.RemoveView(inner);
                    allSequences.Remove(seqUi);
                    PersistRingConfig();
                })
                .SetNegativeButton(Resource.String.cancel, (_, _) => { })
                .Create();
            dlg.Show();
            dlg.GetButton((int)DialogButtonType.Positive)?.SetAllCaps(false);
            dlg.GetButton((int)DialogButtonType.Negative)?.SetAllCaps(false);
        };

        seqUi.ActionSpinner = MakeActionSpinner();
        seqUi.ActionSpinner.Visibility = ViewStates.Gone;
        inner.AddView(seqUi.ActionSpinner);

        seqUi.ActionPkgEdit = new EditText(activity) { Visibility = ViewStates.Gone };
        seqUi.ActionPkgEdit.InputType = InputTypes.ClassText;
        seqUi.ActionPkgEdit.SetSingleLine(true);

        seqUi.ActionIntentEdit = new EditText(activity) { Visibility = ViewStates.Gone };
        seqUi.ActionIntentEdit.InputType = InputTypes.ClassText;
        seqUi.ActionIntentEdit.SetSingleLine(true);

        seqUi.ActionUrlEdit = new EditText(activity) { Visibility = ViewStates.Gone };
        seqUi.ActionUrlEdit.InputType = InputTypes.ClassText;
        seqUi.ActionUrlEdit.SetSingleLine(true);

        void SyncActSummary()
        {
            actValueText.Text = FormatCompactActionSummary(seqUi.ActionSpinner, seqUi.ActionPkgEdit, seqUi.ActionIntentEdit, seqUi.ActionUrlEdit);
        }

        if (seq?.Action is { Kind: var actKind } act && actKind != RingActionKind.None)
        {
            SetSpinnerSelected(seqUi.ActionSpinner, actKind);
            seqUi.ActionPkgEdit.Text = act.LaunchPackageName ?? "";
            seqUi.ActionIntentEdit.Text = act.IntentAction ?? "";
            seqUi.ActionUrlEdit.Text = act.UrlString ?? "";
        }
        else
        {
            SetSpinnerSelected(seqUi.ActionSpinner, RingActionKind.LaunchApp);
            seqUi.ActionPkgEdit.Text = RingConfig.DefaultCameraLaunchPackageName;
            seqUi.ActionIntentEdit.Text = "";
            seqUi.ActionUrlEdit.Text = "";
        }

        seqUi.ActionSpinner.ItemSelected += (_, _) =>
        {
            SyncActSummary();
            PersistRingConfig();
        };
        SyncActSummary();

        actValueHit.Click += (_, _) =>
        {
            var labels = ActionKindItems.Select(i => activity.GetString(i.LabelResId)).ToArray();
            var builder = new Android.App.AlertDialog.Builder(activity)
                .SetItems(labels, (_, e) =>
                {
                    seqUi.ActionSpinner.SetSelection(e.Which);
                    var kind = GetSpinnerSelectedKind(seqUi.ActionSpinner);
                    if (kind != RingActionKind.LaunchApp)
                        seqUi.ActionPkgEdit.Text = "";
                    if (kind != RingActionKind.BroadcastIntentAction)
                        seqUi.ActionIntentEdit.Text = "";
                    if (kind != RingActionKind.OpenUrl)
                        seqUi.ActionUrlEdit.Text = "";
                    SyncActSummary();
                    PersistRingConfig();
                    if (kind == RingActionKind.LaunchApp)
                    {
                        actValueHit.Post(() => LauncherAppPickerHelper.Show(activity, p =>
                        {
                            if (p != null)
                            {
                                seqUi.ActionPkgEdit.Text = p;
                                SyncActSummary();
                                PersistRingConfig();
                            }
                        }));
                    }
                    else if (kind == RingActionKind.BroadcastIntentAction)
                    {
                        actValueHit.Post(() => ShowBroadcastActionDialog(seqUi.ActionIntentEdit, () =>
                        {
                            SyncActSummary();
                            PersistRingConfig();
                        }));
                    }
                    else if (kind == RingActionKind.OpenUrl)
                    {
                        actValueHit.Post(() => ShowUrlInputDialog(seqUi.ActionUrlEdit, () =>
                        {
                            SyncActSummary();
                            PersistRingConfig();
                        }));
                    }
                })
                .SetNegativeButton(Resource.String.cancel, (_, _) => { });
            var dlg = builder.Create();
            dlg.Show();
            dlg.GetButton((int)DialogButtonType.Negative)?.SetAllCaps(false);
        };

        WirePersistSequence(seqUi);
        seqUi.ActionPkgEdit.TextChanged += (_, _) => SyncActSummary();
        seqUi.ActionIntentEdit.TextChanged += (_, _) => SyncActSummary();
        seqUi.ActionUrlEdit.TextChanged += (_, _) => SyncActSummary();
        return seqUi;
    }

    public void BuildInto(LinearLayout container, RingConfig config)
    {
        suppressPersist = true;
        editedConfig = config;
        dp = activity.Resources?.DisplayMetrics?.Density ?? 2.5f;
        var initialRadio = ModeToRadioId(config.ExecutionMode);
        var initialProfile = ProfileForRadio(config, initialRadio);

        // ── Ring preview (shared with main / per-app editor) ──
        var ringBlock = new LinearLayout(activity) { Orientation = Orientation.Vertical };
        ringCanvas = new RingVisualizationCanvas(activity);
        ringImage = new ImageView(activity);
        ringImage.SetScaleType(ImageView.ScaleType.FitCenter);
        var ringH = RingVisualizationCanvas.PreferredHeightPx(activity);
        ringBlock.AddView(ringImage, new LinearLayout.LayoutParams(LinearLayout.LayoutParams.MatchParent, ringH));
        ringFadeRunnable = new Java.Lang.Runnable(() =>
        {
            if (ringCanvas == null || ringImage == null) return;
            var more = ringCanvas.TickFade();
            RedrawRingBitmapInternal();
            if (more && ringFadeRunnable != null)
                ringFadeHandler.PostDelayed(ringFadeRunnable, 30);
        });
        ringImage.Post(() => RedrawRingBitmapInternal());
        container.AddView(ringBlock);

        // ── Mode tabs (Leica camera style) ──
        var modeItems = new[] {
            (activity.GetString(Resource.String.ring_mode_accum_repeat), AccumulateTabRadioId),
            (activity.GetString(Resource.String.ring_mode_threshold), ThresholdTabRadioId),
            (activity.GetString(Resource.String.ring_mode_each), EachTabRadioId),
            (activity.GetString(Resource.String.ring_mode_gesture), GestureTabRadioId),
        };

        var mainSettingsPanel = new LinearLayout(activity) { Orientation = Orientation.Vertical };

        var tabContainer = new LinearLayout(activity) { Orientation = Orientation.Vertical };
        tabContainer.SetGravity(GravityFlags.CenterHorizontal);

        var indicatorBar = new View(activity);
        indicatorBar.SetBackgroundColor(Clr(Resource.Color.md_theme_onSurfaceVariant));
        var barLp = new LinearLayout.LayoutParams((int)(28 * dp), (int)(3 * dp));
        barLp.Gravity = GravityFlags.CenterHorizontal;
        barLp.BottomMargin = (int)(8 * dp);
        tabContainer.AddView(indicatorBar, barLp);

        var tabScrollHost = new FrameLayout(activity);
        var tabScrollHostLp = new LinearLayout.LayoutParams(
            LinearLayout.LayoutParams.MatchParent, LinearLayout.LayoutParams.WrapContent);
        tabContainer.AddView(tabScrollHost, tabScrollHostLp);

        var tabScroll = new HorizontalScrollView(activity);
        tabScroll.HorizontalScrollBarEnabled = false;
        tabScroll.FillViewport = true;
        tabScroll.OverScrollMode = OverScrollMode.Never;
        tabScrollHost.AddView(tabScroll, new FrameLayout.LayoutParams(
            FrameLayout.LayoutParams.MatchParent, FrameLayout.LayoutParams.WrapContent));

        var tabTouchBlocker = new View(activity);
        tabTouchBlocker.Clickable = true;
        tabTouchBlocker.SetBackgroundColor(Color.Transparent);
        tabTouchBlocker.Visibility = ViewStates.Gone;
        tabScrollHost.AddView(tabTouchBlocker, new FrameLayout.LayoutParams(
            FrameLayout.LayoutParams.MatchParent, FrameLayout.LayoutParams.MatchParent));

        Java.Lang.Runnable? hideTabScrollBlockerRunnable = null;
        void ScheduleHideTabScrollBlocker()
        {
            if (tabTouchBlocker.Visibility != ViewStates.Visible) return;
            if (hideTabScrollBlockerRunnable != null)
                tabScroll.RemoveCallbacks(hideTabScrollBlockerRunnable);
            hideTabScrollBlockerRunnable = new Java.Lang.Runnable(() =>
            {
                tabTouchBlocker.Visibility = ViewStates.Gone;
                hideTabScrollBlockerRunnable = null;
            });
            tabScroll.PostDelayed(hideTabScrollBlockerRunnable, 120);
        }

        var tabRow = new LinearLayout(activity) { Orientation = Orientation.Horizontal };
        tabRow.SetGravity(GravityFlags.Center);
        tabScroll.AddView(tabRow);

        var tabLabels = new Dictionary<int, TextView>();
        foreach (var (text, id) in modeItems)
        {
            var tv = new TextView(activity);
            tv.Text = text;
            tv.TextSize = 13;
            tv.SetTextColor(Clr(Resource.Color.md_theme_outline));
            tv.SetPadding((int)(16 * dp), (int)(4 * dp), (int)(16 * dp), (int)(4 * dp));
            tv.Gravity = GravityFlags.Center;
            tv.Clickable = true;
            tv.Focusable = true;
            tabRow.AddView(tv);
            tabLabels[id] = tv;
        }

        var radioGroup = new RadioGroup(activity);
        radioGroup.Visibility = ViewStates.Gone;
        var rbAccum = new RadioButton(activity) { Id = AccumulateTabRadioId };
        var rbThresh = new RadioButton(activity) { Id = ThresholdTabRadioId };
        var rbEach = new RadioButton(activity) { Id = EachTabRadioId };
        var rbGesture = new RadioButton(activity) { Id = GestureTabRadioId };
        radioGroup.AddView(rbAccum);
        radioGroup.AddView(rbThresh);
        radioGroup.AddView(rbEach);
        radioGroup.AddView(rbGesture);
        radioGroup.Check(initialRadio);

        var modeOptions = new LinearLayout(activity) { Orientation = Orientation.Vertical };

        var thresholdSection = new LinearLayout(activity) { Orientation = Orientation.Vertical };
        var minAbsInit = Math.Clamp(initialProfile.MinAbsToTrigger, 1, 2000);
        var minAbsStepper = AddIntStepperSettingRow(thresholdSection, Resource.String.ring_settings_sensitivity_section,
            10, 2000, minAbsInit, 0);
        WirePersistOnIntStepper(minAbsStepper);

        var accumTimeoutSection = new LinearLayout(activity) { Orientation = Orientation.Vertical };
        var timeoutMsInit = (int)Math.Clamp(initialProfile.AccumulationTimeoutMs, 1, 999);
        var accumTimeoutStepper = AddIntStepperSettingRow(accumTimeoutSection, Resource.String.ring_settings_accum_timeout,
            1, 999, timeoutMsInit, 0);
        WirePersistOnIntStepper(accumTimeoutStepper);

        modeOptions.AddView(thresholdSection);
        var accumTimeoutSectionLp = new LinearLayout.LayoutParams(
            LinearLayout.LayoutParams.MatchParent, LinearLayout.LayoutParams.WrapContent);
        accumTimeoutSectionLp.TopMargin = (int)(12 * dp);
        modeOptions.AddView(accumTimeoutSection, accumTimeoutSectionLp);
        var modeOptsAttachLp = new LinearLayout.LayoutParams(
            LinearLayout.LayoutParams.MatchParent, LinearLayout.LayoutParams.WrapContent);
        modeOptsAttachLp.TopMargin = (int)(14 * dp);
        mainSettingsPanel.AddView(modeOptions, modeOptsAttachLp);

        var gestureTabPanel = new LinearLayout(activity) { Orientation = Orientation.Vertical };

        void UpdateModeOptionsVisibility()
        {
            var id = radioGroup.CheckedRadioButtonId;
            var onGesture = id == GestureTabRadioId;
            mainSettingsPanel.Visibility = onGesture ? ViewStates.Gone : ViewStates.Visible;
            gestureTabPanel.Visibility = onGesture ? ViewStates.Visible : ViewStates.Gone;
            thresholdSection.Visibility = (!onGesture && (id == AccumulateTabRadioId || id == ThresholdTabRadioId)) ? ViewStates.Visible : ViewStates.Gone;
            accumTimeoutSection.Visibility = (!onGesture && (id == AccumulateTabRadioId || id == ThresholdTabRadioId)) ? ViewStates.Visible : ViewStates.Gone;
        }

        void ApplyTabLabelSelectionStyle(int selectedId)
        {
            foreach (var (id, tv) in tabLabels)
            {
                bool sel = id == selectedId;
                tv.SetTextColor(Clr(sel
                    ? Resource.Color.md_theme_onSurface
                    : Resource.Color.md_theme_outline));
                tv.SetTypeface(Typeface.Default, sel ? TypefaceStyle.Bold : TypefaceStyle.Normal);
            }
        }

        void EnsureTabRowSidePaddingForCentering()
        {
            if (tabScroll.Width <= 0)
            {
                tabScroll.Post(EnsureTabRowSidePaddingForCentering);
                return;
            }
            var half = tabScroll.Width / 2;
            tabRow.SetPadding(half, 0, half, 0);
        }

        var programmaticTabScroll = false;

        var tabScrollXAtDown = 0;
        var tabStripHadHorizontalScroll = false;
        Java.Lang.Runnable? tabStripAlignIdleRunnable = null;

        void OnTabStripPointer(MotionEvent? ev)
        {
            if (ev == null) return;
            var a = ev.ActionMasked;
            if (a == MotionEventActions.Down)
            {
                if (tabStripAlignIdleRunnable != null)
                {
                    tabScroll.RemoveCallbacks(tabStripAlignIdleRunnable);
                    tabStripAlignIdleRunnable = null;
                }
                tabStripPointerDown = true;
                tabScrollXAtDown = tabScroll.ScrollX;
                tabStripHadHorizontalScroll = false;
            }
            else if (a is MotionEventActions.Up or MotionEventActions.Cancel)
            {
                var hadHorizontal = tabStripHadHorizontalScroll;
                tabStripPointerDown = false;
                tabStripHadHorizontalScroll = false;
                if (hadHorizontal)
                {
                    tabScroll.Post(() =>
                    {
                        var nearest = FindNearestTabId();
                        if (nearest != radioGroup.CheckedRadioButtonId)
                            SwitchTab(nearest);
                        else
                            ScrollSelectedTabToCenter(false);
                    });
                }
                else
                    tabScroll.Post(() =>
                    {
                        if (!programmaticTabScroll)
                            ScheduleTabStripAlignIdle();
                    });
            }
        }

        void ScrollSelectedTabToCenter(bool blockTouchesWhileScrolling = false)
        {
            int checkedId = radioGroup.CheckedRadioButtonId;
            if (!tabLabels.TryGetValue(checkedId, out var tv)) return;
            if (tabScroll.Width <= 0 || tv.Width <= 0)
            {
                var captureBlock = blockTouchesWhileScrolling;
                tabScroll.Post(() => ScrollSelectedTabToCenter(captureBlock));
                return;
            }
            var centerX = tv.Left + tv.Width / 2;
            var target = centerX - tabScroll.Width / 2;
            var maxScroll = Math.Max(0, tabRow.Width - tabScroll.Width);
            var clamped = Math.Clamp(target, 0, maxScroll);
            if (clamped == tabScroll.ScrollX)
            {
                if (blockTouchesWhileScrolling)
                    tabTouchBlocker.Visibility = ViewStates.Gone;
                return;
            }
            if (blockTouchesWhileScrolling)
                tabTouchBlocker.Visibility = ViewStates.Visible;
            programmaticTabScroll = true;
            tabScroll.SmoothScrollTo(clamped, 0);
            tabScroll.PostDelayed(() => { programmaticTabScroll = false; }, 340);
            if (blockTouchesWhileScrolling)
                ScheduleHideTabScrollBlocker();
        }

        int FindNearestTabId()
        {
            if (tabScroll.Width <= 0)
                return radioGroup.CheckedRadioButtonId;
            var centerLine = tabScroll.ScrollX + tabScroll.Width / 2f;
            var bestId = radioGroup.CheckedRadioButtonId;
            var bestDist = float.MaxValue;
            foreach (var (id, tv) in tabLabels)
            {
                if (tv.Width <= 0) continue;
                var c = tv.Left + tv.Width / 2f;
                var d = Math.Abs(c - centerLine);
                if (d < bestDist)
                {
                    bestDist = d;
                    bestId = id;
                }
            }
            return bestId;
        }

        void SwitchTab(int newId)
        {
            var oldId = radioGroup.CheckedRadioButtonId;
            if (oldId == newId) return;
            suppressPersist = true;
            var cfgTab = editedConfig;
            if (oldId == GestureTabRadioId)
                SaveUiToProfile(cfgTab, GestureTabRadioId);
            else
                SaveUiToProfile(cfgTab, oldId);
            cfgTab.ExecutionMode = newId == GestureTabRadioId
                ? RingExecutionMode.Gesture
                : RadioIdToMode(newId);
            saveFullConfig(cfgTab);
            radioGroup.Check(newId);
            LoadProfileIntoUi(cfgTab, newId);
            UpdateModeOptionsVisibility();
            ApplyTabLabelSelectionStyle(radioGroup.CheckedRadioButtonId);
            tabScroll.Post(() => ScrollSelectedTabToCenter(true));
            suppressPersist = false;
        }

        void RunTabStripAlignIdle()
        {
            tabStripAlignIdleRunnable = null;
            if (programmaticTabScroll || tabStripPointerDown)
                return;
            ScrollSelectedTabToCenter(false);
        }

        void ScheduleTabStripAlignIdle()
        {
            if (programmaticTabScroll || tabStripPointerDown)
                return;
            if (tabStripAlignIdleRunnable != null)
                tabScroll.RemoveCallbacks(tabStripAlignIdleRunnable);
            tabStripAlignIdleRunnable = new Java.Lang.Runnable(RunTabStripAlignIdle);
            tabScroll.PostDelayed(tabStripAlignIdleRunnable, 200);
        }

        tabStripScrollRef = tabScroll;
        tabStripTouchDispatchHook = OnTabStripPointer;

        tabScroll.ScrollChange += (_, _) =>
        {
            if (!programmaticTabScroll && tabStripPointerDown &&
                Math.Abs(tabScroll.ScrollX - tabScrollXAtDown) > 0)
                tabStripHadHorizontalScroll = true;

            ScheduleHideTabScrollBlocker();
            if (!programmaticTabScroll)
                ApplyTabLabelSelectionStyle(FindNearestTabId());
            if (!programmaticTabScroll && !tabStripPointerDown)
                ScheduleTabStripAlignIdle();
        };

        foreach (var (id, tv) in tabLabels)
            tv.Click += (_, _) => SwitchTab(id);
        ApplyTabLabelSelectionStyle(radioGroup.CheckedRadioButtonId);
        tabScroll.Post(() =>
        {
            EnsureTabRowSidePaddingForCentering();
            tabScroll.Post(() => ScrollSelectedTabToCenter(false));
        });
        radioGroup.CheckedChange += (_, _) => UpdateModeOptionsVisibility();

        var tabContainerLp = new LinearLayout.LayoutParams(
            LinearLayout.LayoutParams.MatchParent, LinearLayout.LayoutParams.WrapContent);
        tabContainerLp.TopMargin = (int)(20 * dp);
        tabContainerLp.BottomMargin = (int)(8 * dp);
        container.AddView(tabContainer, tabContainerLp);

        // ── Normal left/right (compact) ──
        AddCompactDirectionRow(mainSettingsPanel, Resource.String.ring_direction_left, initialProfile.Normal.Left,
            out var leftDefault, out var leftSummary);
        AddCompactDirectionRow(mainSettingsPanel, Resource.String.ring_direction_right, initialProfile.Normal.Right,
            out var rightDefault, out var rightSummary);

        var mainSettingsLp = new LinearLayout.LayoutParams(
            LinearLayout.LayoutParams.MatchParent, LinearLayout.LayoutParams.WrapContent);
        container.AddView(mainSettingsPanel, mainSettingsLp);

        this.minAbsStepper = minAbsStepper;
        this.radioGroup = radioGroup;
        this.accumTimeoutStepper = accumTimeoutStepper;
        this.leftDefault = leftDefault;
        this.rightDefault = rightDefault;
        this.leftActionSummary = leftSummary;
        this.rightActionSummary = rightSummary;

        // ── Gestures (Gesture tab) ──
        var gestureTimeoutInit = (int)Math.Clamp(initialProfile.GestureSequenceTimeoutMs, 1, 2000);
        var gestureSequenceTimeoutStepper = AddIntStepperSettingRow(gestureTabPanel, Resource.String.ring_settings_accum_timeout,
            1, 2000, gestureTimeoutInit, 8);
        WirePersistOnIntStepper(gestureSequenceTimeoutStepper);
        this.gestureSequenceTimeoutStepper = gestureSequenceTimeoutStepper;

        var seqHdrLp = new LinearLayout.LayoutParams(
            LinearLayout.LayoutParams.MatchParent, LinearLayout.LayoutParams.WrapContent);
        seqHdrLp.TopMargin = (int)(12 * dp);
        var seqContainer = new LinearLayout(activity) { Orientation = Orientation.Vertical };
        gestureTabPanel.AddView(seqContainer, seqHdrLp);
        this.seqContainer = seqContainer;
        var allSequences = new List<SequenceUi>();
        for (int i = 0; i < initialProfile.Normal.Sequences.Count; i++)
        {
            var su = BuildSequenceUi(initialProfile.Normal.Sequences[i], seqContainer, allSequences);
            var suLp = new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MatchParent, LinearLayout.LayoutParams.WrapContent);
            suLp.BottomMargin = (int)(16 * dp);
            seqContainer.AddView(su.BlockRoot, suLp);
            allSequences.Add(su);
        }

        var addSeqBtn = MakeOutlinedButton(activity.GetString(Resource.String.ring_sequence_add_new));
        var addSeqLp = new LinearLayout.LayoutParams(
            LinearLayout.LayoutParams.MatchParent, LinearLayout.LayoutParams.WrapContent);
        addSeqLp.TopMargin = (int)(8 * dp);
        gestureTabPanel.AddView(addSeqBtn, addSeqLp);
        addSeqBtn.Click += (_, _) =>
        {
            var su = BuildSequenceUi(null, seqContainer, allSequences);
            var suLp = new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MatchParent, LinearLayout.LayoutParams.WrapContent);
            suLp.BottomMargin = (int)(16 * dp);
            seqContainer.AddView(su.BlockRoot, suLp);
            allSequences.Add(su);
            PersistRingConfig();
        };

        var gestureTabLp = new LinearLayout.LayoutParams(
            LinearLayout.LayoutParams.MatchParent, LinearLayout.LayoutParams.WrapContent);
        container.AddView(gestureTabPanel, gestureTabLp);
        container.AddView(radioGroup);
        UpdateModeOptionsVisibility();

        this.allSequences = allSequences;
        suppressPersist = false;
    }

    public void DispatchTouchForTabStrip(MotionEvent? ev)
    {
        if (ev != null && tabStripScrollRef != null && ev.PointerCount > 0)
        {
            var r = new Rect();
            var hasRect = tabStripScrollRef.GetGlobalVisibleRect(r);
#pragma warning disable CA1416
            var x = (int)ev.GetRawX(0);
            var y = (int)ev.GetRawY(0);
#pragma warning restore CA1416
            var a = ev.ActionMasked;
            var inStrip = hasRect && r.Contains(x, y);
            if (inStrip)
                tabStripTouchDispatchHook?.Invoke(ev);
            else if (tabStripPointerDown && a is MotionEventActions.Up or MotionEventActions.Cancel)
                tabStripTouchDispatchHook?.Invoke(ev);
        }
    }

    public void OnPausePersist()
    {
        persistHandler.RemoveCallbacks(persistRunnable!);
        PersistRingConfig();
    }

    public void OnDestroyCleanup()
    {
        ringFadeHandler.RemoveCallbacks(ringFadeRunnable!);
        ringBitmap?.Recycle();
        ringBitmap = null;
        ringCanvas = null;
        ringImage = null;
        ringFadeRunnable = null;
        tabStripScrollRef = null;
        tabStripTouchDispatchHook = null;
        tabStripPointerDown = false;
    }

    void RedrawRingBitmapInternal()
    {
        if (ringCanvas == null || ringImage == null) return;
        var w = ringImage.Width;
        var h = ringImage.Height;
        if (w <= 0 || h <= 0) return;
        if (ringBitmap == null || ringBitmap.Width != w || ringBitmap.Height != h)
        {
            ringBitmap?.Recycle();
            ringBitmap = Bitmap.CreateBitmap(w, h, Bitmap.Config.Argb8888!);
        }
        var canvas = new Canvas(ringBitmap);
        ringBitmap.EraseColor(0);
        ringCanvas.Draw(canvas, w, h);
        ringImage.SetImageBitmap(ringBitmap);
    }

    /// <summary> Feed optical-tracking samples from the activity sensor callback to animate the ring preview. </summary>
    public void FeedRingSensorPreview(RingSensorData sensorData)
    {
        if (ringCanvas == null) return;
        var startFade = ringCanvas.SetValue(sensorData);
        RedrawRingBitmapInternal();
        if (startFade && ringFadeRunnable != null)
            ringFadeHandler.Post(ringFadeRunnable);
    }
}
