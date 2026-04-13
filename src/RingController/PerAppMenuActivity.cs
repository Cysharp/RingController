using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.OS;
using Android.Views;
using Android.Widget;
using System;
using System.Linq;

namespace RingController;

[Activity(Label = "@string/per_app_menu_title", Theme = "@style/AppTheme", Exported = false)]
public class PerAppMenuActivity : Activity
{
    LinearLayout? listSection;
    float dp;

    Color Clr(int colorRes) => new(GetColor(colorRes));

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        dp = Resources?.DisplayMetrics?.Density ?? 2.5f;

        var root = new LinearLayout(this) { Orientation = Orientation.Vertical };
        root.SetFitsSystemWindows(true);
        root.SetBackgroundColor(Clr(Resource.Color.md_theme_surface));

        ActivityHeaderBar.Attach(this, root, GetString(Resource.String.per_app_menu_title), dp, Clr);

        var scroll = new ScrollView(this);
        scroll.SetClipToPadding(false);
        root.AddView(scroll, new LinearLayout.LayoutParams(
            LinearLayout.LayoutParams.MatchParent, 0, 1f));

        var pad = (int)(16 * dp);
        var content = new LinearLayout(this) { Orientation = Orientation.Vertical };
        content.SetPadding(pad, pad, pad, pad + (int)(24 * dp));
        scroll.AddView(content);

        var addBtn = new Button(this) { Text = GetString(Resource.String.per_app_add) };
        addBtn.SetAllCaps(false);
        addBtn.SetBackgroundResource(Resource.Drawable.button_outlined);
        addBtn.SetTextColor(Clr(Resource.Color.md_theme_primary));
        addBtn.Click += (_, _) =>
        {
            LauncherAppPickerHelper.Show(this, pkg =>
            {
                if (pkg == null) return;
                var cfgRoot = RingConfigStore.LoadOrCreate(this);
                if (!cfgRoot.PerAppOverrides.ContainsKey(pkg))
                {
                    var tpl = RingConfigStore.Clone(cfgRoot);
                    tpl.PerAppOverrides.Clear();
                    cfgRoot.PerAppOverrides[pkg] = tpl;
                    RingConfigStore.Save(this, cfgRoot);
                }
                LocalActivityIntent.Start(this, typeof(PerAppOverrideActivity), i =>
                    i.PutExtra(PerAppOverrideActivity.ExtraPackageName, pkg));
            });
        };
        content.AddView(addBtn, new LinearLayout.LayoutParams(
            LinearLayout.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

        listSection = new LinearLayout(this) { Orientation = Orientation.Vertical };
        var listSectionLp = new LinearLayout.LayoutParams(
            LinearLayout.LayoutParams.MatchParent, LinearLayout.LayoutParams.WrapContent);
        listSectionLp.TopMargin = (int)(12 * dp);
        content.AddView(listSection, listSectionLp);

        SetContentView(root);
    }

    protected override void OnResume()
    {
        base.OnResume();
        RebuildOverrideList();
    }

    void RebuildOverrideList()
    {
        if (listSection == null) return;
        listSection.RemoveAllViews();
        var cfg = RingConfigStore.LoadOrCreate(this);
        var pkgs = cfg.PerAppOverrides.Keys.OrderBy(p => GetAppLabel(p) ?? p, StringComparer.CurrentCultureIgnoreCase).ToList();
        if (pkgs.Count == 0)
        {
            var empty = new TextView(this)
            {
                Text = GetString(Resource.String.per_app_empty),
                TextSize = 14f
            };
            empty.SetTextColor(Clr(Resource.Color.md_theme_onSurfaceVariant));
            var emptyLp = new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
            emptyLp.TopMargin = (int)(4 * dp);
            listSection.AddView(empty, emptyLp);
            return;
        }

        var iconSize = (int)(40 * dp);
        var deleteBtnSize = (int)(48 * dp);

        foreach (var pkg in pkgs)
        {
            var col = new LinearLayout(this) { Orientation = Orientation.Vertical };
            var title = new TextView(this) { TextSize = 15f };
            title.SetTextColor(Clr(Resource.Color.md_theme_onSurface));
            title.Text = GetAppLabel(pkg) ?? pkg;
            col.AddView(title, new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
            var sub = new TextView(this) { TextSize = 12f };
            sub.SetTextColor(Clr(Resource.Color.md_theme_onSurfaceVariant));
            sub.Text = pkg;
            var subLp = new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
            subLp.TopMargin = (int)(2 * dp);
            col.AddView(sub, subLp);

            var iconIv = new ImageView(this);
            iconIv.SetScaleType(ImageView.ScaleType.CenterCrop);
            var iconDraw = TryLoadAppIcon(pkg);
            if (iconDraw != null)
                iconIv.SetImageDrawable(iconDraw);
            else
            {
                iconIv.SetBackgroundColor(Clr(Resource.Color.md_theme_surfaceContainerHigh));
                iconIv.SetImageDrawable(null);
            }
            var iconLp = new LinearLayout.LayoutParams(iconSize, iconSize);
            iconLp.RightMargin = (int)(12 * dp);

            var mainArea = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            mainArea.SetGravity(GravityFlags.CenterVertical);
            mainArea.Clickable = true;
            mainArea.Focusable = true;
            mainArea.AddView(iconIv, iconLp);
            mainArea.AddView(col, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));

            var pkgCopy = pkg;
            mainArea.Click += (_, _) =>
            {
                LocalActivityIntent.Start(this, typeof(PerAppOverrideActivity), i =>
                    i.PutExtra(PerAppOverrideActivity.ExtraPackageName, pkgCopy));
            };

            var deleteBtn = new ImageButton(this);
            deleteBtn.SetImageResource(Resource.Drawable.ic_delete);
            deleteBtn.SetColorFilter(Clr(Resource.Color.md_theme_error), PorterDuff.Mode.SrcIn!);
            deleteBtn.ContentDescription = GetString(Resource.String.per_app_delete_content_desc);
            deleteBtn.SetPadding((int)(10 * dp), (int)(10 * dp), (int)(10 * dp), (int)(10 * dp));
            deleteBtn.SetScaleType(ImageView.ScaleType.Center);
            var delTa = ObtainStyledAttributes(new[] { Android.Resource.Attribute.SelectableItemBackgroundBorderless });
            deleteBtn.Background = delTa.GetDrawable(0);
            delTa.Recycle();
            deleteBtn.Click += (_, _) =>
            {
                new AlertDialog.Builder(this)
                    .SetMessage(GetString(Resource.String.per_app_remove_confirm))
                    .SetPositiveButton(Resource.String.remove, (_, _) =>
                    {
                        var r = RingConfigStore.LoadOrCreate(this);
                        r.PerAppOverrides.Remove(pkgCopy);
                        RingConfigStore.Save(this, r);
                        RebuildOverrideList();
                    })
                    .SetNegativeButton(Resource.String.cancel, (_, _) => { })
                    .Show();
            };

            var row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            row.SetGravity(GravityFlags.CenterVertical);
            row.SetPadding(0, (int)(6 * dp), 0, (int)(6 * dp));
            row.AddView(mainArea, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
            row.AddView(deleteBtn, new LinearLayout.LayoutParams(deleteBtnSize, deleteBtnSize));

            listSection.AddView(row, new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

            var div = new View(this);
            div.SetBackgroundColor(Clr(Resource.Color.md_theme_outlineVariant));
            var divLp = new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MatchParent, (int)Math.Max(1, dp));
            listSection.AddView(div, divLp);
        }
    }

    Android.Graphics.Drawables.Drawable? TryLoadAppIcon(string? pkg)
    {
        if (string.IsNullOrWhiteSpace(pkg)) return null;
        try
        {
            var pm = PackageManager;
            if (pm == null) return null;
            var info = pm.GetApplicationInfo(pkg!, (PackageInfoFlags)0);
            return info.LoadIcon(pm);
        }
        catch
        {
            return null;
        }
    }

    string? GetAppLabel(string? pkg)
    {
        if (string.IsNullOrWhiteSpace(pkg)) return null;
        try
        {
            var pm = PackageManager;
            if (pm == null) return null;
            var info = pm.GetApplicationInfo(pkg!, (PackageInfoFlags)0);
            return info.LoadLabel(pm)?.ToString();
        }
        catch
        {
            return null;
        }
    }
}
