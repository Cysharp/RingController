using Android.Content;
using Android.Graphics;
using Android.Views;

namespace RingController;

/// <summary>
/// Ring visualization state; draws to a Bitmap for ImageView (no custom View).
/// </summary>
public sealed class RingVisualizationCanvas
{
    readonly float displayDensity;
    readonly Paint minorTickPaint;
    readonly Paint glowTickPaint;
    readonly Paint deltaTextPaint;

    float angle;
    readonly float[] tickGlows = new float[60];
    bool isFading;
    float lastSumDeltaX;
    float textAlpha;

    public const int Ticks = 60;

    public RingVisualizationCanvas(Context context)
    {
        displayDensity = context.Resources?.DisplayMetrics?.Density ?? 2.5f;

        minorTickPaint = new Paint(PaintFlags.AntiAlias);
        minorTickPaint.SetStyle(Paint.Style.Stroke);
        minorTickPaint.StrokeWidth = 2f * displayDensity;
        minorTickPaint.StrokeCap = Paint.Cap.Round;
        minorTickPaint.Color = Color.ParseColor("#333333");

        glowTickPaint = new Paint(PaintFlags.AntiAlias);
        glowTickPaint.SetStyle(Paint.Style.Stroke);
        glowTickPaint.StrokeWidth = 2.5f * displayDensity;
        glowTickPaint.StrokeCap = Paint.Cap.Round;
        glowTickPaint.Color = Color.White;

        deltaTextPaint = new Paint(PaintFlags.AntiAlias);
        deltaTextPaint.TextAlign = Paint.Align.Center;
        deltaTextPaint.TextSize = 48 * displayDensity;
        deltaTextPaint.Color = Color.ParseColor("#C0C0C0");
        deltaTextPaint.SetTypeface(Typeface.Create("sans-serif-light", TypefaceStyle.Normal));
    }

    public static int PreferredHeightPx(Context context)
    {
        var dp = context.Resources?.DisplayMetrics?.Density ?? 2.5f;
        return (int)(280 * dp);
    }

    public bool SetValue(in RingSensorData sensorData)
    {
        bool startFade = false;
        var absoluteAngle = ((sensorData.AbsoluteAngle % 360f) + 360f) % 360f;
        var sum_delta_x = sensorData.sum_delta_x;

        if (sum_delta_x != 0f && angle != 0f)
        {
            lastSumDeltaX = sum_delta_x * -1f;
            textAlpha = 1f;

            float diff = absoluteAngle - angle;
            while (diff > 180f) diff -= 360f;
            while (diff < -180f) diff += 360f;

            int steps = (int)(Math.Abs(diff) / 2f) + 2;
            for (int i = 0; i <= steps; i++)
            {
                float t = i / (float)steps;
                float a = angle + diff * t;
                a = ((a % 360f) + 360f) % 360f;
                int tickIdx = (int)Math.Round(a / (360f / Ticks)) % Ticks;
                tickGlows[tickIdx] = 1.0f;
            }

            if (!isFading)
            {
                isFading = true;
                startFade = true;
            }
        }
        else
        {
            int currentTick = (int)Math.Round(absoluteAngle / (360f / Ticks)) % Ticks;
            tickGlows[currentTick] = 1.0f;
            if (!isFading)
            {
                isFading = true;
                startFade = true;
            }
        }

        angle = absoluteAngle;
        return startFade;
    }

    /// <summary>One fade tick; returns true if more frames are needed.</summary>
    public bool TickFade()
    {
        bool needsFade = false;

        if (textAlpha > 0f)
        {
            textAlpha *= 0.85f;
            if (textAlpha < 0.01f) textAlpha = 0f;
            if (textAlpha > 0f) needsFade = true;
        }

        for (int i = 0; i < Ticks; i++)
        {
            if (tickGlows[i] > 0f)
            {
                tickGlows[i] *= 0.82f;
                if (tickGlows[i] < 0.01f) tickGlows[i] = 0f;
                if (tickGlows[i] > 0f) needsFade = true;
            }
        }

        if (!needsFade)
            isFading = false;

        return needsFade;
    }

    public void Draw(Canvas canvas, int width, int height)
    {
        float cx = width / 2f;
        float cy = height / 2f;
        float R = MathF.Min(width, height) / 2f - 8 * displayDensity;

        float tickR = R - 8 * displayDensity;
        for (int i = 0; i < Ticks; i++)
        {
            canvas.Save();
            canvas.Rotate(i * (360f / Ticks), cx, cy);

            float len = 12 * displayDensity;

            canvas.DrawLine(cx, cy - tickR, cx, cy - tickR + len, minorTickPaint);

            if (tickGlows[i] > 0.01f)
            {
                int alpha = (int)(Math.Min(1f, tickGlows[i]) * 255);
                glowTickPaint.Alpha = alpha;
                canvas.DrawLine(cx, cy - tickR, cx, cy - tickR + len, glowTickPaint);
            }

            canvas.Restore();
        }

        if (textAlpha > 0.01f)
        {
            deltaTextPaint.Alpha = (int)(textAlpha * 255);
            float textOffset = (deltaTextPaint.Descent() + deltaTextPaint.Ascent()) / 2f;
            int displayVal = (int)Math.Round(lastSumDeltaX);
            string txt = displayVal > 0 ? $"+{displayVal}" : displayVal.ToString();
            canvas.DrawText(txt, cx, cy - textOffset, deltaTextPaint);
        }
    }
}
