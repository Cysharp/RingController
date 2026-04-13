using Android.App;
using Android.Content;
using Android.Graphics;
using Android.OS;
using Android.Views;
using Android.Widget;

namespace RingController;

/// <summary> General app settings: backup (import/export) and lock-screen ring behavior. </summary>
[Activity(Label = "@string/settings_title", Theme = "@style/AppTheme", Exported = false)]
public class SettingsActivity : Activity
{
    const int RequestImport = 1001;
    const int RequestExport = 1002;

    Color Clr(int colorRes) => new(GetColor(colorRes));

    CheckBox? runWhenLockedCheck;
    float dp;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        dp = Resources?.DisplayMetrics?.Density ?? 2.5f;

        var root = new LinearLayout(this) { Orientation = Orientation.Vertical };
        root.SetFitsSystemWindows(true);
        root.SetBackgroundColor(Clr(Resource.Color.md_theme_surface));

        ActivityHeaderBar.Attach(this, root, GetString(Resource.String.settings_title), dp, Clr);

        var scroll = new ScrollView(this);
        scroll.SetClipToPadding(false);
        root.AddView(scroll, new LinearLayout.LayoutParams(
            LinearLayout.LayoutParams.MatchParent, 0, 1f));

        var pad = (int)(16 * dp);
        var content = new LinearLayout(this) { Orientation = Orientation.Vertical };
        content.SetPadding(pad, pad, pad, pad + (int)(24 * dp));
        scroll.AddView(content);

        var cfg = RingConfigStore.LoadOrCreate(this);

        runWhenLockedCheck = new CheckBox(this)
        {
            Text = GetString(Resource.String.settings_run_when_locked),
            TextSize = 15f,
            Checked = cfg.RunWhenDeviceLocked
        };
        runWhenLockedCheck.SetTextColor(Clr(Resource.Color.md_theme_onSurface));
        runWhenLockedCheck.SetPadding(0, (int)(2 * dp), 0, (int)(2 * dp));
        runWhenLockedCheck.CheckedChange += (_, e) =>
        {
            var c = RingConfigStore.LoadOrCreate(this);
            c.RunWhenDeviceLocked = e.IsChecked;
            RingConfigStore.Save(this, c);
        };
        content.AddView(runWhenLockedCheck, new LinearLayout.LayoutParams(
            LinearLayout.LayoutParams.MatchParent, LinearLayout.LayoutParams.WrapContent));

        content.AddView(MakeDivider(), DividerLayoutParams());

        content.AddView(MakeSectionHeader(Resource.String.settings_section_backup));

        var importBtn = new Button(this) { Text = GetString(Resource.String.settings_import) };
        importBtn.SetAllCaps(false);
        importBtn.SetBackgroundResource(Resource.Drawable.button_outlined);
        importBtn.SetTextColor(Clr(Resource.Color.md_theme_primary));
        var importLp = new LinearLayout.LayoutParams(
            LinearLayout.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        importLp.TopMargin = (int)(8 * dp);
        importBtn.Click += (_, _) =>
        {
            var intent = new Intent(Intent.ActionOpenDocument);
            intent.AddCategory(Intent.CategoryOpenable);
            intent.SetType("*/*");
            intent.PutExtra(Intent.ExtraMimeTypes, new[] { "application/json", "text/plain" });
            StartActivityForResult(intent, RequestImport);
        };
        content.AddView(importBtn, importLp);

        var exportBtn = new Button(this) { Text = GetString(Resource.String.settings_export) };
        exportBtn.SetAllCaps(false);
        exportBtn.SetBackgroundResource(Resource.Drawable.button_outlined);
        exportBtn.SetTextColor(Clr(Resource.Color.md_theme_primary));
        var exportLp = new LinearLayout.LayoutParams(
            LinearLayout.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        exportLp.TopMargin = (int)(8 * dp);
        exportBtn.Click += (_, _) =>
        {
            var intent = new Intent(Intent.ActionCreateDocument);
            intent.AddCategory(Intent.CategoryOpenable);
            intent.SetType("application/json");
            intent.PutExtra(Intent.ExtraTitle, "ring_config.json");
            StartActivityForResult(intent, RequestExport);
        };
        content.AddView(exportBtn, exportLp);

        SetContentView(root);
    }

    TextView MakeSectionHeader(int stringId)
    {
        var tv = new TextView(this) { Text = GetString(stringId), TextSize = 13f };
        tv.SetTextColor(Clr(Resource.Color.md_theme_primary));
        return tv;
    }

    View MakeDivider()
    {
        var div = new View(this);
        div.SetBackgroundColor(Clr(Resource.Color.md_theme_outlineVariant));
        return div;
    }

    LinearLayout.LayoutParams DividerLayoutParams()
    {
        var lp = new LinearLayout.LayoutParams(LinearLayout.LayoutParams.MatchParent, (int)Math.Max(1, dp));
        lp.TopMargin = (int)(16 * dp);
        lp.BottomMargin = (int)(16 * dp);
        return lp;
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (resultCode != Result.Ok || data?.Data == null) return;
        var uri = data.Data!;

        if (requestCode == RequestImport)
        {
            try
            {
                using var stream = ContentResolver?.OpenInputStream(uri);
                if (stream == null)
                {
                    Toast.MakeText(this, Resource.String.settings_import_failed, ToastLength.Long)?.Show();
                    return;
                }
                using var reader = new StreamReader(stream);
                var json = reader.ReadToEnd();
                if (RingConfigStore.TryImportFromJson(this, json, out var err))
                {
                    Toast.MakeText(this, Resource.String.settings_import_success, ToastLength.Long)?.Show();
                    ReloadRunWhenLockedFromStore();
                }
                else
                {
                    var msg = string.IsNullOrEmpty(err)
                        ? GetString(Resource.String.settings_import_failed)
                        : err;
                    Toast.MakeText(this, msg, ToastLength.Long)?.Show();
                }
            }
            catch (Exception ex)
            {
                Toast.MakeText(this, ex.Message, ToastLength.Long)?.Show();
            }
            return;
        }

        if (requestCode == RequestExport)
        {
            try
            {
                var json = RingConfigStore.LoadConfigJson(this);
                using var stream = ContentResolver?.OpenOutputStream(uri);
                if (stream == null)
                {
                    Toast.MakeText(this, Resource.String.settings_export_failed, ToastLength.Long)?.Show();
                    return;
                }
                using var writer = new StreamWriter(stream);
                writer.Write(json);
                Toast.MakeText(this, Resource.String.settings_export_success, ToastLength.Long)?.Show();
            }
            catch (Exception ex)
            {
                Toast.MakeText(this, ex.Message, ToastLength.Long)?.Show();
            }
        }
    }

    void ReloadRunWhenLockedFromStore()
    {
        if (runWhenLockedCheck == null) return;
        var c = RingConfigStore.LoadOrCreate(this);
        runWhenLockedCheck.Checked = c.RunWhenDeviceLocked;
    }
}
