using Android;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.Hardware;
using Android.OS;
using Android.Runtime;
using Android.Views;
using Android.Widget;

namespace RingController;

[Activity(Label = "@string/per_app_override_title", Theme = "@style/AppTheme", Exported = false)]
public class PerAppOverrideActivity : Activity, ISensorEventListener
{
    public const string ExtraPackageName = "package";

    SensorManager? sensorManager;
    RingSensorModel? ringSensor;
    RingSettingsPanelHost? ringPanelHost;

    Color Clr(int colorRes) => new(GetColor(colorRes));

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        var pkg = Intent?.GetStringExtra(ExtraPackageName)?.Trim() ?? "";
        if (string.IsNullOrEmpty(pkg))
        {
            Finish();
            return;
        }

        var dp = Resources?.DisplayMetrics?.Density ?? 2.5f;
        var pad = (int)(16 * dp);

        var root = new LinearLayout(this) { Orientation = Orientation.Vertical };
        root.SetFitsSystemWindows(true);
        root.SetBackgroundColor(Clr(Resource.Color.md_theme_surface));

        ActivityHeaderBar.Attach(this, root, GetAppLabel(pkg) ?? pkg, dp, Clr);

        var scroll = new ScrollView(this);
        scroll.SetClipToPadding(false);
        root.AddView(scroll, new LinearLayout.LayoutParams(
            LinearLayout.LayoutParams.MatchParent, 0, 1f));

        var container = new LinearLayout(this) { Orientation = Orientation.Vertical };
        container.SetPadding(pad, pad, pad, pad + (int)(28 * dp));
        scroll.AddView(container);

        var rootCfg = RingConfigStore.LoadOrCreate(this);
        if (!rootCfg.PerAppOverrides.TryGetValue(pkg, out var entry) || entry == null)
        {
            entry = RingConfigStore.CloneForPerAppEntry(RingConfigStore.LoadOrCreate(this));
            rootCfg.PerAppOverrides[pkg] = entry;
            RingConfigStore.Save(this, rootCfg);
        }

        ringPanelHost = new RingSettingsPanelHost(this, cfg =>
        {
            var r = RingConfigStore.LoadOrCreate(this);
            r.PerAppOverrides[pkg] = RingConfigStore.CloneForPerAppEntry(cfg);
            RingConfigStore.Save(this, r);
        });
        ringPanelHost.BuildInto(container, entry);

        SetContentView(root);

        sensorManager = (SensorManager?)GetSystemService(SensorService);
        ringSensor = RingSensorModel.GetSensor(sensorManager);

        if (Build.VERSION.SdkInt >= BuildVersionCodes.M)
        {
            if (CheckSelfPermission(Manifest.Permission.Camera) != Permission.Granted)
                RequestPermissions([Manifest.Permission.Camera], 0);
        }
    }

    string? GetAppLabel(string packageName)
    {
        try
        {
            var pm = PackageManager;
            if (pm == null) return null;
            var info = pm.GetApplicationInfo(packageName, (PackageInfoFlags)0);
            return info.LoadLabel(pm)?.ToString();
        }
        catch
        {
            return null;
        }
    }

    public override bool DispatchTouchEvent(MotionEvent? ev)
    {
        ringPanelHost?.DispatchTouchForTabStrip(ev);
        return base.DispatchTouchEvent(ev!);
    }

    protected override void OnResume()
    {
        base.OnResume();
        if (ringSensor != null)
            sensorManager!.RegisterListener(this, ringSensor.Sensor, SensorDelay.Fastest);
    }

    protected override void OnPause()
    {
        ringPanelHost?.OnPausePersist();
        base.OnPause();
        sensorManager?.UnregisterListener(this);
    }

    protected override void OnDestroy()
    {
        ringPanelHost?.OnDestroyCleanup();
        ringPanelHost = null;
        base.OnDestroy();
    }

    public void OnSensorChanged(SensorEvent? e)
    {
        if (ringSensor == null || e?.Values == null) return;
        var sensorData = ringSensor.CreateRingSensorData(e);
        ringPanelHost?.FeedRingSensorPreview(sensorData);
    }

    public void OnAccuracyChanged(Sensor? sensor, [GeneratedEnum] SensorStatus accuracy) { }
}
