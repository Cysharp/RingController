using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Runtime;
using Android.Util;
using Android.Widget;
using System;
using System.Collections.Generic;
using System.Linq;

namespace RingController;

// QUERY_ALL_PACKAGES: installed apps with a launch intent (GitHub APK direct distribution).
// Icons: Java RingAppPickerAdapter — C# must not subclass BaseAdapter on CoreCLR.
public static class LauncherAppPickerHelper
{
    const string Tag = "RingAppPicker";
    const string JniAdapterClass = "com/cysharp/ringcontroller/RingAppPickerAdapter";
    const string AdapterCtorSig =
        "(Landroid/content/Context;Landroid/content/pm/PackageManager;[Ljava/lang/String;[Ljava/lang/String;III)V";

    public static List<(string Label, string Package)> QueryLaunchableApps(PackageManager? pm)
    {
        var empty = new List<(string Label, string Package)>();
        if (pm == null) return empty;

        var rows = new List<(string Label, string Package)>();
        try
        {
            IList<ApplicationInfo> installed = pm.GetInstalledApplications((PackageInfoFlags)0);
            foreach (var app in installed)
            {
                var pkg = app.PackageName;
                if (string.IsNullOrEmpty(pkg)) continue;
                if (pm.GetLaunchIntentForPackage(pkg) == null) continue;
                var label = app.LoadLabel(pm)?.ToString() ?? pkg;
                rows.Add((label, pkg));
            }
        }
        catch (Exception ex)
        {
            Log.Error(Tag, "GetInstalledApplications: " + ex);
        }

        return rows
            .OrderBy(r => r.Label, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public static IListAdapter? CreateListAdapter(Activity activity, List<(string Label, string Package)> rows)
    {
        ArgumentNullException.ThrowIfNull(activity);
        if (rows.Count == 0) return null;

        var labels = rows.Select(r => r.Label).ToArray();
        var packages = rows.Select(r => r.Package).ToArray();
        var pm = activity.PackageManager;
        if (pm == null) return null;

        try
        {
            var labelArr = JNIEnv.NewArray(labels);
            var pkgArr = JNIEnv.NewArray(packages);
            var layoutId = (int)Resource.Layout.ring_app_picker_item;
            var iconId = (int)Resource.Id.ring_app_picker_icon;
            var labelId = (int)Resource.Id.ring_app_picker_label;
            var handle = JNIEnv.CreateInstance(
                JniAdapterClass,
                AdapterCtorSig,
                new JValue(activity),
                new JValue(pm),
                new JValue(labelArr),
                new JValue(pkgArr),
                new JValue(layoutId),
                new JValue(iconId),
                new JValue(labelId));
            return Java.Lang.Object.GetObject<IListAdapter>(handle, JniHandleOwnership.TransferLocalRef);
        }
        catch (Exception ex)
        {
            Log.Error(Tag, "RingAppPickerAdapter JNI: " + ex);
            return null;
        }
    }

    /// <summary> Picked package, or <c>null</c> if the user cancelled (restore previous selection in UI). </summary>
    public static void Show(Activity activity, Action<string?> onPackageResult)
    {
        ArgumentNullException.ThrowIfNull(activity);
        var pm = activity.PackageManager;
        if (pm == null) return;

        var rows = QueryLaunchableApps(pm);

        if (rows.Count == 0)
        {
            var emptyDlg = new AlertDialog.Builder(activity)
                .SetMessage(activity.GetString(Resource.String.ring_app_pick_empty) ?? "")
                .SetPositiveButton(Resource.String.cancel, (_, _) => { })
                .Create();
            emptyDlg.Show();
            emptyDlg.GetButton((int)DialogButtonType.Positive)?.SetAllCaps(false);
            return;
        }

        var labels = rows.Select(r => r.Label).ToArray();
        var listAdapter = CreateListAdapter(activity, rows);

        var finished = false;
        void Finish(string? package)
        {
            if (finished) return;
            finished = true;
            onPackageResult(package);
        }

        var builder = new AlertDialog.Builder(activity)
            .SetNegativeButton(Resource.String.cancel, (_, _) => Finish(null));

        if (listAdapter != null)
        {
            builder.SetAdapter(listAdapter, (_, e) =>
            {
                var idx = e?.Which ?? -1;
                if (idx < 0 || idx >= rows.Count) return;
                Finish(rows[idx].Package);
            });
        }
        else
        {
            builder.SetItems(labels, (_, e) =>
            {
                var idx = e?.Which ?? -1;
                if (idx < 0 || idx >= rows.Count) return;
                Finish(rows[idx].Package);
            });
        }

        var dlg = builder.Create();
        // CoreCLR: do not use a C# subclass of Java.Lang.Object for OnCancelListener (no Java peer).
        dlg.DismissEvent += (_, _) =>
        {
            if (!finished) Finish(null);
        };
        dlg.Show();
        dlg.GetButton((int)DialogButtonType.Negative)?.SetAllCaps(false);
    }
}
