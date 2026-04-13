using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Util;

namespace RingController;

/// <summary> In-app navigation via explicit <see cref="ComponentName"/> (avoids broken JNI Class.FromType for some activities). </summary>
static class LocalActivityIntent
{
    const string Tag = "LocalActivityIntent";
    const string FallbackJniPrefix = "crc647dff40f1f97d0d21.";

    static string? jniClassPrefix;

    static string GetJniClassPrefix()
    {
        if (jniClassPrefix != null)
            return jniClassPrefix;
        try
        {
            var mainClassName = Java.Lang.Class.FromType(typeof(MainActivity)).Name;
            if (string.IsNullOrEmpty(mainClassName) || mainClassName == "java.lang.Object")
            {
                jniClassPrefix = FallbackJniPrefix;
                return jniClassPrefix;
            }

            var dot = mainClassName.LastIndexOf('.');
            jniClassPrefix = dot >= 0 ? mainClassName.Substring(0, dot + 1) : FallbackJniPrefix;
        }
        catch (Exception ex)
        {
            Log.Warn(Tag, "GetJniClassPrefix: " + ex.Message);
            jniClassPrefix = FallbackJniPrefix;
        }

        return jniClassPrefix;
    }

    /// <summary> Uses installed manifest entries so the class name always matches PackageManager (no JNI peer lookup). </summary>
    static string? ResolveClassNameFromInstalledPackage(Activity from, Type activityType)
    {
        var pm = from.PackageManager;
        var pkg = from.PackageName;
        if (pm == null || pkg == null) return null;
        var suffix = "." + activityType.Name;
        try
        {
            var pi = pm.GetPackageInfo(pkg, PackageInfoFlags.Activities);
            var infos = pi?.Activities;
            if (infos == null) return null;
            foreach (var ai in infos)
            {
                var n = ai?.Name;
                if (n != null && n.EndsWith(suffix, StringComparison.Ordinal))
                    return n;
            }
        }
        catch (Exception ex)
        {
            Log.Warn(Tag, "ResolveClassNameFromInstalledPackage: " + ex.Message);
        }

        return null;
    }

    public static void Start(Activity from, Type activityType)
    {
        Start(from, activityType, null);
    }

    public static void Start(Activity from, Type activityType, Action<Intent>? configure)
    {
        var pkg = from.PackageName;
        if (pkg == null) return;
        var className = ResolveClassNameFromInstalledPackage(from, activityType)
            ?? GetJniClassPrefix() + activityType.Name;
        var cn = new ComponentName(pkg, className);
        var intent = new Intent().SetComponent(cn);
        configure?.Invoke(intent);
        from.StartActivity(intent);
    }
}
