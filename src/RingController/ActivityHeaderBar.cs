using Android.App;
using Android.Graphics;
using Android.Views;
using Android.Widget;

namespace RingController;

static class ActivityHeaderBar
{
    public static void Attach(Activity activity, LinearLayout root, string title, float dp, Func<int, Color> toColor)
    {
        var row = new LinearLayout(activity) { Orientation = Orientation.Horizontal };
        row.SetGravity(GravityFlags.CenterVertical);
        row.SetMinimumHeight((int)(56 * dp));

        var backTa = activity.ObtainStyledAttributes(new[] { Android.Resource.Attribute.SelectableItemBackgroundBorderless });
        var back = new ImageButton(activity);
        back.SetImageResource(Resource.Drawable.ic_arrow_back);
        var ripple = backTa.GetDrawable(0);
        if (ripple != null)
            back.Background = ripple;
        backTa.Recycle();
        var pb = (int)(8 * dp);
        back.SetPadding(pb, pb, pb, pb);
        back.Click += (_, _) => activity.Finish();

        var titleTv = new TextView(activity) { Text = title, TextSize = 18f };
        titleTv.SetTextColor(toColor(Resource.Color.md_theme_onSurface));

        row.AddView(back, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));
        var titleLp = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
        titleLp.LeftMargin = (int)(4 * dp);
        row.AddView(titleTv, titleLp);

        root.AddView(row, new LinearLayout.LayoutParams(
            LinearLayout.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
    }
}
