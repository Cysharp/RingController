using Android.OS;

namespace RingController;

public sealed class RingGestureInterpreter
{
    /// <summary> Driver sum often stays in a small band instead of 0; matches accum / gesture resync logic. </summary>
    internal const int DriverNeutralAbsBand = 8;

    sealed record GestureEvent(long TimeMs, RingDirection Direction, int AbsDeltaX);

    readonly List<GestureEvent> buffer = new();
    readonly List<GestureSequenceState> gestureSequenceStates = new();
    int? lastGestureSumForDriver;
    /// <summary>
    /// Time of the last sensor callback. After <see cref="ResetTimeoutClocksForSensorEvent"/> resets to 0,
    /// updated in <see cref="InterpretGesture"/>'s <c>finally</c> (elapsed time restarts per event).
    /// </summary>
    long lastGestureSensorMs;
    long lastActionAtMs;

    /// <summary> Resets internal timeout/cooldown clocks to 0 on each sensor event (call from the service every time). </summary>
    internal void ResetTimeoutClocksForSensorEvent()
    {
        lastGestureSensorMs = 0;
        lastActionAtMs = 0;
    }

    struct GestureSequenceState
    {
        /// <summary> Accumulation for the current step in the active direction only (left: positive delta_x; right: absolute of negative delta_x). </summary>
        public int DirectionalAccum;
        public int StepIndex;
    }

    /// <summary>
    /// Contribution toward the current step direction for this frame. Pass only positive <paramref name="directionalDelta"/> / positive <paramref name="directionalDSum"/>.
    /// <c>dSum</c> can far exceed <c>delta</c> when accumulated <c>sum</c> built in the opposite direction unwinds in one frame ("release");
    /// adding that whole amount to the next step's MinAbs would satisfy only the last step with a small actual rotation. Prefer <c>delta</c>;
    /// on release, accumulate only the <c>delta</c> portion. When <c>delta</c> is 0, take <c>dSum</c> only up to a small cap (edge-case catch-up).
    /// </summary>
    static int DirectionalGestureIncrement(int directionalDelta, int directionalDSum)
    {
        var d = directionalDelta;
        var s = directionalDSum;
        const int microPickCap = 24;
        if (d == 0)
            return Math.Min(s, microPickCap);
        // If sum-diff dominates, it is almost always global integral unwind, not "this step" rotation.
        const int dSumOverDeltaUnwindRatio = 3;
        if (s > d * dSumOverDeltaUnwindRatio)
            return d;
        return Math.Max(d, s);
    }

    static int GestureLeftIncrement(int deltaX, int dSum) =>
        DirectionalGestureIncrement(Math.Max(0, deltaX), Math.Max(0, dSum));

    static int GestureRightIncrement(int deltaX, int dSum) =>
        DirectionalGestureIncrement(Math.Max(0, -deltaX), Math.Max(0, -dSum));

    /// <summary>
    /// Gesture mode: accumulates per-frame <c>delta_x</c> and sum deltas per direction (<see cref="DirectionalGestureIncrement"/> dampens release overshoot).
    /// </summary>
    public RingActionConfig? InterpretGesture(
        int sumDeltaX,
        int deltaX,
        long nowMs,
        RingModeProfile modeProfile)
    {
        var gapSincePrevSensor = lastGestureSensorMs > 0 ? nowMs - lastGestureSensorMs : 0L;
        try
        {
            return InterpretGestureCore(sumDeltaX, deltaX, nowMs, gapSincePrevSensor, modeProfile);
        }
        finally
        {
            lastGestureSensorMs = nowMs;
        }
    }

    RingActionConfig? InterpretGestureCore(
        int sumDeltaX,
        int deltaX,
        long nowMs,
        long gapSincePrevSensor,
        RingModeProfile modeProfile)
    {
        var ctx = modeProfile.Normal;

        var prevForDsum = lastGestureSumForDriver;
        var dSum = prevForDsum.HasValue ? sumDeltaX - prevForDsum.Value : 0;

        if (lastGestureSumForDriver.HasValue)
        {
            var prev = lastGestureSumForDriver.Value;
            const int minPrevForDriverZero = 48;
            if (Math.Abs(sumDeltaX) <= DriverNeutralAbsBand && Math.Abs(prev) >= minPrevForDriverZero)
            {
                lastGestureSumForDriver = sumDeltaX;
                // Sharp reversals often make the driver snap sum to 0. Keep the step; only redo accumulation for this step.
                ResyncGestureAccumAfterDriverZero();
                return null;
            }
            var d = sumDeltaX - prev;
            const int maxPlausibleStep = 32768;
            if (Math.Abs(d) > maxPlausibleStep)
            {
                lastGestureSumForDriver = sumDeltaX;
                gestureSequenceStates.Clear();
                return null;
            }
        }
        lastGestureSumForDriver = sumDeltaX;

        if (ctx.Sequences.Count == 0)
            return null;

        while (gestureSequenceStates.Count < ctx.Sequences.Count)
        {
            gestureSequenceStates.Add(new GestureSequenceState
            {
                DirectionalAccum = 0,
                StepIndex = 0,
            });
        }
        while (gestureSequenceStates.Count > ctx.Sequences.Count)
            gestureSequenceStates.RemoveAt(gestureSequenceStates.Count - 1);

        for (var si = 0; si < ctx.Sequences.Count; si++)
        {
            var seq = ctx.Sequences[si];
            var steps = seq.Steps;
            var n = steps.Count;
            if (n < 1)
                continue;

            var s = gestureSequenceStates[si];

            // MaxGapMs: large gap since last callback = drop the current gesture attempt.
            // During continuous sampling the gap stays small, so we do not reset.
            if (seq.MaxGapMs > 0 && lastGestureSensorMs > 0 && gapSincePrevSensor > seq.MaxGapMs)
            {
                ResetGestureState(ref s);
                gestureSequenceStates[si] = s;
                continue;
            }

            var step = steps[s.StepIndex];
            var wantLeft = step.Direction == RingDirection.Left;
            var prevDirectionalAccum = s.DirectionalAccum;
            if (wantLeft)
                s.DirectionalAccum += GestureLeftIncrement(deltaX, dSum);
            else
                s.DirectionalAccum += GestureRightIncrement(deltaX, dSum);

            var fired = false;
            if (s.DirectionalAccum >= step.MinAbs)
            {
                if (step.MaxAbs == null || s.DirectionalAccum <= step.MaxAbs.Value)
                    fired = true;
                else if (prevDirectionalAccum < step.MinAbs)
                    fired = true; // one frame jumped past MaxAbs; still count (small MinAbs / MaxAbs windows)
            }

            if (!fired)
            {
                gestureSequenceStates[si] = s;
                continue;
            }

            var isLastStep = s.StepIndex == n - 1;
            if (isLastStep)
            {
                if (nowMs - lastActionAtMs < modeProfile.ActionCooldownMs)
                {
                    gestureSequenceStates[si] = s;
                    continue;
                }

                lastActionAtMs = nowMs;
                buffer.Clear();
                ResetGestureState(ref s);
                gestureSequenceStates[si] = s;
                return seq.Action;
            }

            s.StepIndex++;
            s.DirectionalAccum = 0;
            gestureSequenceStates[si] = s;
        }

        return null;
    }

    void ResyncGestureAccumAfterDriverZero()
    {
        for (var i = 0; i < gestureSequenceStates.Count; i++)
        {
            var s = gestureSequenceStates[i];
            s.DirectionalAccum = 0;
            gestureSequenceStates[i] = s;
        }
    }

    static void ResetGestureState(ref GestureSequenceState s)
    {
        s.StepIndex = 0;
        s.DirectionalAccum = 0;
    }

    public RingActionConfig? Interpret(
        int absDeltaX,
        RingDirection direction,
        RingModeProfile modeProfile,
        RingExecutionMode executionMode)
    {
        // ThresholdCrossing: magnitude gate; service blends sum/delta and rate-limits before calling here.
        if (executionMode == RingExecutionMode.ThresholdCrossing)
        {
            if (absDeltaX < modeProfile.MinAbsToTrigger)
                return null;
        }
        else if (executionMode != RingExecutionMode.EachEvent)
        {
            if (absDeltaX < modeProfile.MinAbsToTrigger) return null;
        }

        var now = SystemClock.UptimeMillis();

        while (buffer.Count > 0 && now - buffer[0].TimeMs > modeProfile.SequenceBufferWindowMs)
            buffer.RemoveAt(0);

        buffer.Add(new GestureEvent(now, direction, absDeltaX));

        var ctx = modeProfile.Normal;

        foreach (var seq in ctx.Sequences)
        {
            var steps = seq.Steps;
            var n = steps.Count;
            if (n <= 0) continue;
            if (buffer.Count < n) continue;

            var startIdx = buffer.Count - n;
            var first = buffer[startIdx];
            var last = buffer[buffer.Count - 1];

            if (seq.MaxTotalMs > 0 && last.TimeMs - first.TimeMs > seq.MaxTotalMs)
                continue;

            var ok = true;
            for (var i = 0; i < n; i++)
            {
                var ev = buffer[startIdx + i];
                var st = steps[i];

                if (ev.Direction != st.Direction)
                {
                    ok = false;
                    break;
                }

                if (ev.AbsDeltaX < st.MinAbs)
                {
                    ok = false;
                    break;
                }

                // MaxAbs is not enforced as a hard ceiling: |sum| often exceeds a tight MaxAbs in one callback
                // once MinAbs is satisfied (same as gesture step overshoot).

                if (seq.MaxGapMs > 0 && i > 0)
                {
                    var prevEv = buffer[startIdx + i - 1];
                    if (ev.TimeMs - prevEv.TimeMs > seq.MaxGapMs)
                    {
                        ok = false;
                        break;
                    }
                }
            }

            if (!ok) continue;

            if (executionMode is not RingExecutionMode.ThresholdCrossing
                and not RingExecutionMode.ThresholdAccumulatedRepeat
                && now - lastActionAtMs < modeProfile.ActionCooldownMs)
                return null;

            buffer.Clear();
            lastActionAtMs = now;
            return seq.Action;
        }

        var map = direction == RingDirection.Right ? ctx.Right : ctx.Left;
        var action = map.GetActionForAbs(absDeltaX);
        if (action.Kind == RingActionKind.None)
            return null;

        if (executionMode is not RingExecutionMode.ThresholdCrossing
            and not RingExecutionMode.ThresholdAccumulatedRepeat
            && now - lastActionAtMs < modeProfile.ActionCooldownMs)
            return null;

        lastActionAtMs = now;
        return action;
    }
}
