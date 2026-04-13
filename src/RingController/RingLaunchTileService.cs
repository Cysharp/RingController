using Android.App;
using Android.Content;
using Android.OS;
using Android.Service.QuickSettings;

namespace RingController;

/// <summary> Quick Settings tile — MIUI / Leitz Phone: use 24dp white-filled vector (<c>ic_qs_tile_ring</c>), same pattern as working third-party tiles. </summary>
[Service(
    Name = "com.cysharp.RingController.RingLaunchTileService",
    Exported = true,
    Icon = "@drawable/ic_qs_tile_ring",
    Label = "@string/app_name",
    Permission = "android.permission.BIND_QUICK_SETTINGS_TILE")]
[IntentFilter(new[] { TileService.ActionQsTile })]
public class RingLaunchTileService : TileService
{
    public override void OnClick()
    {
        var intent = new Intent(this, typeof(MainActivity));
        intent.SetFlags(ActivityFlags.NewTask | ActivityFlags.ClearTop | ActivityFlags.SingleTop);

        if (Build.VERSION.SdkInt >= BuildVersionCodes.UpsideDownCake)
        {
            var flags = PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable;
            var pi = PendingIntent.GetActivity(this, 0, intent, flags);
            StartActivityAndCollapse(pi);
        }
        else
        {
            StartActivityAndCollapse(intent);
        }
    }
}
