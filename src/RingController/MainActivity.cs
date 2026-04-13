using System.Reflection;
using Android;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.Hardware;
using Android.OS;
using Android.Runtime;
using Android.Text;
using Android.Views;
using Android.Widget;

namespace RingController;

[Activity(Label = "@string/app_name", MainLauncher = true,
    Theme = "@style/AppTheme")]
public class MainActivity : Activity, ISensorEventListener
{
    SensorManager? sensorManager;
    RingSensorModel? ringSensor;

    TextView? statusDot;
    TextView? statusMessage;
    LinearLayout? brightnessPermRow;

    RingSettingsPanelHost? ringPanelHost;

    float dp;

    Color Clr(int colorRes) => new(GetColor(colorRes));

    Button MakeTextButton(string text, bool error = false)
    {
        var btn = new Button(this) { Text = text };
        btn.SetAllCaps(false);
        var ta = ObtainStyledAttributes(new[] { Android.Resource.Attribute.SelectableItemBackgroundBorderless });
        btn.Background = ta.GetDrawable(0);
        ta.Recycle();
        btn.SetTextColor(Clr(error ? Resource.Color.md_theme_error : Resource.Color.md_theme_primary));
        btn.SetPadding((int)(12 * dp), 0, (int)(12 * dp), 0);
        return btn;
    }

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        dp = Resources?.DisplayMetrics?.Density ?? 2.5f;
        int pad = (int)(16 * dp);
        var config = RingConfigStore.LoadOrCreate(this);

        var outer = new LinearLayout(this) { Orientation = Orientation.Vertical };
        outer.SetFitsSystemWindows(true);
        outer.SetBackgroundColor(Clr(Resource.Color.md_theme_surface));

        var scroll = new ScrollView(this);
        scroll.SetClipToPadding(false);
        outer.AddView(scroll, new LinearLayout.LayoutParams(
            LinearLayout.LayoutParams.MatchParent, 0, 1f));

        var container = new LinearLayout(this) { Orientation = Orientation.Vertical };
        container.SetPadding(pad, pad, pad, pad + (int)(28 * dp));
        scroll.AddView(container);

        var statusStrip = new LinearLayout(this) { Orientation = Orientation.Vertical };
        var statusStripLp = new LinearLayout.LayoutParams(
            LinearLayout.LayoutParams.MatchParent, LinearLayout.LayoutParams.WrapContent);
        statusStripLp.BottomMargin = (int)(12 * dp);
        container.AddView(statusStrip, statusStripLp);

        var statusRow = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        statusRow.SetGravity(GravityFlags.CenterVertical);
        statusRow.SetPadding(0, (int)(2 * dp), 0, (int)(6 * dp));

        statusDot = new TextView(this) { TextSize = 16f };
        statusDot.SetIncludeFontPadding(false);
        statusRow.AddView(statusDot, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));

        statusMessage = new TextView(this) { TextSize = 14f };
        statusMessage.SetPadding((int)(6 * dp), (int)(10 * dp), 0, (int)(10 * dp));
        statusMessage.Clickable = true;
        statusMessage.Focusable = true;
        statusMessage.Click += (_, _) =>
            StartActivity(new Intent(Android.Provider.Settings.ActionAccessibilitySettings));
        statusRow.AddView(statusMessage, new LinearLayout.LayoutParams(
            0, LinearLayout.LayoutParams.WrapContent, 1f));

        var overflowAnchor = new TextView(this) { Text = "\u22EE", TextSize = 22f };
        overflowAnchor.SetIncludeFontPadding(false);
        overflowAnchor.SetTextColor(Clr(Resource.Color.md_theme_onSurface));
        overflowAnchor.SetPadding((int)(8 * dp), (int)(10 * dp), (int)(2 * dp), (int)(10 * dp));
        overflowAnchor.Clickable = true;
        overflowAnchor.Focusable = true;
        var overflowTa = ObtainStyledAttributes(new[] { Android.Resource.Attribute.SelectableItemBackgroundBorderless });
        overflowAnchor.Background = overflowTa.GetDrawable(0);
        overflowTa.Recycle();
        overflowAnchor.Click += (_, _) =>
        {
            var popup = new PopupMenu(this, overflowAnchor);
            popup.MenuInflater.Inflate(Resource.Menu.main_menu, popup.Menu);
            popup.MenuItemClick += (_, e) =>
            {
                switch (e.Item!.ItemId)
                {
                    case Resource.Id.menu_settings:
                        LocalActivityIntent.Start(this, typeof(SettingsActivity));
                        break;
                    case Resource.Id.menu_per_app_override:
                        LocalActivityIntent.Start(this, typeof(PerAppMenuActivity));
                        break;
                    case Resource.Id.menu_version:
                        ShowVersionDialog();
                        break;
                }
            };
            popup.Show();
        };
        statusRow.AddView(overflowAnchor, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));

        statusStrip.AddView(statusRow);

        brightnessPermRow = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        brightnessPermRow.SetGravity(GravityFlags.CenterHorizontal);
        var btnPerm = MakeTextButton(GetString(Resource.String.write_settings_permission_button));
        btnPerm.Click += (_, _) => OpenWriteSettingsPermission();
        brightnessPermRow.AddView(btnPerm);
        var permLp = new LinearLayout.LayoutParams(
            LinearLayout.LayoutParams.MatchParent, LinearLayout.LayoutParams.WrapContent);
        permLp.TopMargin = (int)(2 * dp);
        statusStrip.AddView(brightnessPermRow, permLp);
        UpdateBrightnessPermissionRowVisibility();

        ringPanelHost = new RingSettingsPanelHost(this, cfg =>
        {
            RingConfigStore.Save(this, cfg);
            UpdateBrightnessPermissionRowVisibility();
        });
        ringPanelHost.BuildInto(container, config);

        SetContentView(outer);

        sensorManager = (SensorManager?)GetSystemService(SensorService);
        ringSensor = RingSensorModel.GetSensor(sensorManager);

        if (Build.VERSION.SdkInt >= BuildVersionCodes.M)
        {
            if (CheckSelfPermission(Manifest.Permission.Camera) != Permission.Granted)
                RequestPermissions([Manifest.Permission.Camera], 0);
        }
    }

    public override bool DispatchTouchEvent(MotionEvent? ev)
    {
        ringPanelHost?.DispatchTouchForTabStrip(ev);
        return base.DispatchTouchEvent(ev!);
    }

    protected override void OnDestroy()
    {
        ringPanelHost?.OnDestroyCleanup();
        ringPanelHost = null;
        base.OnDestroy();
    }

    void OpenWriteSettingsPermission()
    {
        if (Android.Provider.Settings.System.CanWrite(this))
        {
            Toast.MakeText(this, "Permission already granted", ToastLength.Short)?.Show();
            return;
        }
        var intent = new Intent(Android.Provider.Settings.ActionManageWriteSettings);
        intent.SetData(Android.Net.Uri.Parse($"package:{PackageName}"));
        StartActivity(intent);
    }

    void UpdateStatus()
    {
        if (statusDot == null || statusMessage == null) return;
        if (RingAccessibilityService.IsRunning)
        {
            statusDot.Text = "\u25CF";
            statusDot.SetTextColor(Clr(Resource.Color.md_theme_leicaRed));
            statusMessage.Text = GetString(Resource.String.ring_service_status_running);
        }
        else
        {
            statusDot.Text = "\u25CB";
            statusDot.SetTextColor(Clr(Resource.Color.md_theme_onSurfaceVariant));
            statusMessage.Text = GetString(Resource.String.ring_service_status_stopped);
        }
        statusMessage.SetTextColor(Clr(Resource.Color.md_theme_primary));
    }

    void UpdateBrightnessPermissionRowVisibility()
    {
        if (brightnessPermRow == null) return;
        var cfg = RingConfigStore.LoadOrCreate(this);
        var wantBrightness = RingConfig.ConfigUsesBrightnessControl(cfg);
        var needGrant = wantBrightness && !Android.Provider.Settings.System.CanWrite(this);
        brightnessPermRow.Visibility = needGrant ? ViewStates.Visible : ViewStates.Gone;
    }

    protected override void OnResume()
    {
        base.OnResume();
        UpdateStatus();
        UpdateBrightnessPermissionRowVisibility();
        if (ringSensor != null)
            sensorManager!.RegisterListener(this, ringSensor.Sensor, SensorDelay.Fastest);
    }

    protected override void OnPause()
    {
        ringPanelHost?.OnPausePersist();
        base.OnPause();
        sensorManager?.UnregisterListener(this);
    }

    public void OnSensorChanged(SensorEvent? e)
    {
        if (ringSensor == null || e?.Values == null) return;
        var sensorData = ringSensor.CreateRingSensorData(e);
        ringPanelHost?.FeedRingSensorPreview(sensorData);
        UpdateStatus();
    }

    public void OnAccuracyChanged(Sensor? sensor, [GeneratedEnum] SensorStatus accuracy) { }

    /// <summary>
    /// Version string from embedded assembly metadata (SDK sets from &lt;Version&gt; / InformationalVersion; CI can override).
    /// </summary>
    static string GetDisplayVersionFromAssembly()
    {
        var asm = Assembly.GetExecutingAssembly();
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
        if (info?.InformationalVersion is { Length: > 0 } iv)
        {
            var plus = iv.IndexOf('+');
            return plus >= 0 ? iv[..plus] : iv;
        }

        return asm.GetName().Version?.ToString() ?? "?";
    }

    void ShowVersionDialog()
    {
        var pm = PackageManager;
        if (pm == null) return;
        var pkg = PackageName;
        if (pkg == null) return;
        Android.Content.PM.PackageInfo info;
        try
        {
            info = pm.GetPackageInfo(pkg, (PackageInfoFlags)0);
        }
        catch
        {
            return;
        }
        var label = ApplicationInfo?.LoadLabel(pm)?.ToString() ?? GetString(Resource.String.app_name);
        // Display version: MSBuild / assembly (InformationalVersion or AssemblyVersion); matches CI -p:Version=...
        var verDisplay = GetDisplayVersionFromAssembly();
        var verCode = Build.VERSION.SdkInt >= BuildVersionCodes.P
            ? (long)info.LongVersionCode
            : info.VersionCode;
        var url = GetString(Resource.String.app_info_url);

        var root = new LinearLayout(this) { Orientation = Orientation.Vertical };
        var dlgPad = (int)(22 * dp);
        root.SetPadding(dlgPad, dlgPad, dlgPad, dlgPad);

        var titleBlock = $"{label}\n\n{GetString(Resource.String.version_label)} {verDisplay} ({verCode})";
        var msgTv = new TextView(this) { Text = titleBlock, TextSize = 15f };
        msgTv.SetTextColor(Clr(Resource.Color.md_theme_onSurface));
        root.AddView(msgTv);

        var urlTv = new TextView(this) { Text = url, TextSize = 15f };
        urlTv.SetTextColor(Clr(Resource.Color.md_theme_primary));
        urlTv.SetPadding(0, (int)(12 * dp), 0, 0);
        urlTv.PaintFlags |= Android.Graphics.PaintFlags.UnderlineText;
        urlTv.Clickable = true;
        urlTv.Focusable = true;
        var urlTa = ObtainStyledAttributes(new[] { Android.Resource.Attribute.SelectableItemBackgroundBorderless });
        urlTv.Background = urlTa.GetDrawable(0);
        urlTa.Recycle();
        urlTv.Click += (_, _) =>
        {
            try
            {
                StartActivity(new Intent(Intent.ActionView, Android.Net.Uri.Parse(url!)));
            }
            catch
            {
                // ignore
            }
        };
        root.AddView(urlTv);

        var btnRow = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        btnRow.SetGravity(GravityFlags.CenterHorizontal);
        var btnRowLp = new LinearLayout.LayoutParams(
            LinearLayout.LayoutParams.MatchParent, LinearLayout.LayoutParams.WrapContent);
        btnRowLp.TopMargin = (int)(20 * dp);
        root.AddView(btnRow, btnRowLp);

        var okBtn = MakeTextButton(GetString(Resource.String.ok));
        okBtn.SetAllCaps(false);
        btnRow.AddView(okBtn, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));

        var dlg = new Android.App.AlertDialog.Builder(this)
            .SetView(root)
            .Create();
        okBtn.Click += (_, _) => dlg.Dismiss();
        dlg.Show();
    }
}
