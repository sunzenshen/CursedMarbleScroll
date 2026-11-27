/*
Ported from kaushr’s macOS MarbleScroll (Interaction.swift and Settings.swift).

MIT License

Copyright (c) 2026 Kaushik Ruparel

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
*/
using System;
using System.Collections.Generic;

namespace MarbleScroll
{
    internal enum ScrollButton { None, Middle, X1, X2 }
    internal enum ScrollState { Normal, ButtonPressed, TemporaryScroll, WaitingForSecondClick, LockedScroll }
    internal enum DecisionKind { Pass, Suppress, Scroll, ReplayClick }

    internal struct ScrollDecision
    {
        public readonly DecisionKind Kind;
        public readonly int XUnits, YUnits;
        // Expiration or cancellation can replay an earlier click alongside the current decision.
        public readonly ScrollButton ReplayButton;

        public ScrollDecision(DecisionKind kind, int xUnits = 0, int yUnits = 0,
            ScrollButton replayButton = ScrollButton.None)
        {
            Kind = kind;
            XUnits = xUnits;
            YUnits = yUnits;
            ReplayButton = replayButton;
        }
    }

    internal sealed class ScrollSettings
    {
        public double Speed { get; set; } = 1;
        public double HorizontalSensitivity { get; set; } = 1;
        public double VerticalSensitivity { get; set; } = 1;
        public double MovementThreshold { get; set; } = 2;
        // A press released within ClickGraceMs that moved less than ClickSlop pixels is a click, not a
        // scroll. Zero turns this off, so scrolling starts at MovementThreshold.
        internal const double DefaultClickSlop = 16, DefaultClickGraceMs = 200;
        public double ClickSlop { get; set; }
        public double ClickGraceMs { get; set; } = DefaultClickGraceMs;
        internal const int DefaultLockWindowMs = 300;
        public int LockWindowMs { get; set; } = DefaultLockWindowMs;

        internal static int BoundLockWindow(int value) { return Math.Max(150, Math.Min(1000, value)); }
        public bool Reverse { get; set; }
        public bool Acceleration { get; set; }
        public bool LockEnabled { get; set; } = true;
        public bool SnapToAxis { get; set; }
    }

    internal sealed class ScrollEngine
    {
        private readonly double horizontal, vertical, threshold, doubleClickMs, clickSlop, clickGraceMs;
        private readonly bool acceleration;
        private readonly HashSet<ScrollButton> consumedDowns = new HashSet<ScrollButton>();
        private bool reverse, lockEnabled, snapToAxis;
        private double speed, motionX, motionY;
        private Axis axis;

        private enum Axis { None, Horizontal, Vertical }
        private double? lastTime;
        private double firstDown, travel, pendingX, pendingY, remainderX, remainderY;
        private ScrollButton trigger;

        // Recent wheel output, used to estimate the velocity at release for momentum.
        internal const double VelocityWindowMs = 75, StaleReleaseMs = 50;
        internal const double MinReleaseVelocity = 1, MaxReleaseVelocity = 25; // Wheel units per ms.
        private const int SampleCount = 32;
        private readonly double[] sampleTime = new double[SampleCount];
        private readonly int[] sampleX = new int[SampleCount], sampleY = new int[SampleCount];
        private int sampleNext, samples;
        private double moveTime, releaseX, releaseY;
        private bool hasRelease;

        public ScrollState State { get; private set; }
        public double? PendingDeadlineMs
        {
            get
            {
                if (State == ScrollState.WaitingForSecondClick)
                    return firstDown + doubleClickMs + 0.001; // Second down at the boundary still locks.
                return null;
            }
        }

        public ScrollEngine(ScrollSettings settings)
        {
            speed = Bound(settings.Speed, 0.1, 10, 1);
            horizontal = Bound(settings.HorizontalSensitivity, 0, 5, 1);
            vertical = Bound(settings.VerticalSensitivity, 0, 5, 1);
            threshold = Bound(settings.MovementThreshold, 0, 30, 2);
            doubleClickMs = ScrollSettings.BoundLockWindow(settings.LockWindowMs);
            clickSlop = Bound(settings.ClickSlop, 0, 100, 0);
            clickGraceMs = Bound(settings.ClickGraceMs, 0, 1000, ScrollSettings.DefaultClickGraceMs);
            reverse = settings.Reverse;
            acceleration = settings.Acceleration;
            lockEnabled = settings.LockEnabled;
            snapToAxis = settings.SnapToAxis;
        }

        public bool IsTrigger(ScrollButton button) { return button != ScrollButton.None && trigger == button; }

        public void SetMotionOptions(double value, bool snap)
        {
            if (speed != value || snapToAxis != snap)
                remainderX = remainderY = 0;
            if (snapToAxis != snap)
                axis = Axis.None;
            speed = value;
            snapToAxis = snap;
        }

        public bool Owns(ScrollButton button) { return consumedDowns.Contains(button); }

        public void ForgetDown(ScrollButton button) { consumedDowns.Remove(button); }

        public void SetReverse(bool value)
        {
            if (reverse != value)
                remainderX = remainderY = 0;
            reverse = value;
        }

        public ScrollDecision SetLockEnabled(bool value)
        {
            lockEnabled = value;
            if (!value && State == ScrollState.WaitingForSecondClick)
                return FinishWaiting(DecisionKind.ReplayClick);
            if (!value && State == ScrollState.LockedScroll)
                State = ScrollState.Normal;
            return new ScrollDecision(DecisionKind.Pass);
        }

        public ScrollDecision ButtonDown(ScrollButton button, double timeMs)
        {
            ScrollButton replay;
            if (!Advance(timeMs, out replay))
                return new ScrollDecision(DecisionKind.Pass);
            if (consumedDowns.Contains(button))
                return new ScrollDecision(DecisionKind.Suppress, replayButton: replay);
            if (button == ScrollButton.None || State == ScrollState.ButtonPressed || State == ScrollState.TemporaryScroll)
                return new ScrollDecision(DecisionKind.Pass, replayButton: replay);

            consumedDowns.Add(button);
            if (State == ScrollState.LockedScroll)
            {
                State = ScrollState.Normal;
            }
            else if (State == ScrollState.WaitingForSecondClick && IsTrigger(button))
            {
                State = ScrollState.LockedScroll;
                ResetMotion();
            }
            else
            {
                if (State == ScrollState.WaitingForSecondClick)
                    replay = FinishWaiting(DecisionKind.Pass).ReplayButton;
                State = ScrollState.ButtonPressed;
                trigger = button;
                firstDown = timeMs;
                travel = pendingX = pendingY = 0;
                ResetMotion();
            }
            return new ScrollDecision(DecisionKind.Suppress, replayButton: replay);
        }

        public ScrollDecision ButtonUp(ScrollButton button, double timeMs)
        {
            ScrollButton replay;
            if (!Advance(timeMs, out replay))
                return new ScrollDecision(DecisionKind.Pass);
            if (!consumedDowns.Remove(button))
                return new ScrollDecision(DecisionKind.Pass, replayButton: replay);

            if (IsTrigger(button))
            {
                if (State == ScrollState.ButtonPressed)
                {
                    bool moved = travel >= threshold;
                    if (moved && timeMs - firstDown >= clickGraceMs)
                    {
                        // Held past the grace period with no further motion: a small scroll, not a click.
                        State = ScrollState.Normal;
                        ScrollDecision flushed = FlushMotion(0, 0);
                        return new ScrollDecision(DecisionKind.Scroll, flushed.XUnits, flushed.YUnits, replay);
                    }
                    pendingX = pendingY = 0;
                    State = ScrollState.WaitingForSecondClick;
                    travel = 0;
                    // A click made while moving is headed elsewhere, so it does not wait for a second click.
                    if (!lockEnabled || moved || timeMs - firstDown > doubleClickMs)
                        return FinishWaiting(DecisionKind.ReplayClick);
                }
                else if (State == ScrollState.TemporaryScroll)
                {
                    CaptureReleaseVelocity(timeMs);
                    State = ScrollState.Normal;
                }
                pendingX = pendingY = 0;
            }
            return new ScrollDecision(DecisionKind.Suppress, replayButton: replay);
        }

        public ScrollDecision Move(double dx, double dy, double timeMs)
        {
            ScrollButton replay;
            if (!Advance(timeMs, out replay))
                return new ScrollDecision(DecisionKind.Pass);
            if (!IsFinite(dx) || !IsFinite(dy))
            {
                Reset();
                return new ScrollDecision(DecisionKind.Pass, replayButton: replay);
            }
            moveTime = timeMs;

            switch (State)
            {
                case ScrollState.WaitingForSecondClick:
                    travel += Distance(dx, dy);
                    if (travel >= threshold)
                        return FinishWaiting(DecisionKind.Pass);
                    break;
                case ScrollState.ButtonPressed:
                    travel += Distance(dx, dy);
                    motionX += Math.Abs(dx);
                    motionY += Math.Abs(dy);
                    pendingX += dx;
                    pendingY += dy;
                    if (travel < threshold || (travel < clickSlop && timeMs - firstDown < clickGraceMs))
                        return new ScrollDecision(DecisionKind.Suppress);
                    State = ScrollState.TemporaryScroll;
                    return FlushMotion(0, 0);
                case ScrollState.TemporaryScroll:
                case ScrollState.LockedScroll:
                    motionX += Math.Abs(dx);
                    motionY += Math.Abs(dy);
                    return Convert(dx, dy);
            }
            return new ScrollDecision(DecisionKind.Pass, replayButton: replay);
        }

        public ScrollDecision Tick(double timeMs)
        {
            ScrollButton replay;
            if (Advance(timeMs, out replay) && replay != ScrollButton.None)
                return new ScrollDecision(DecisionKind.ReplayClick, replayButton: replay);
            return new ScrollDecision(DecisionKind.Pass);
        }

        private bool Advance(double timeMs, out ScrollButton replay)
        {
            replay = ScrollButton.None;
            if (!IsFinite(timeMs) || (lastTime.HasValue && timeMs < lastTime.Value))
            {
                Reset();
                return false;
            }

            lastTime = timeMs;
            if (State == ScrollState.WaitingForSecondClick && timeMs - firstDown > doubleClickMs)
                replay = FinishWaiting(DecisionKind.Pass).ReplayButton;
            return true;
        }

        private ScrollDecision FinishWaiting(DecisionKind kind)
        {
            State = ScrollState.Normal;
            return new ScrollDecision(kind, replayButton: trigger);
        }

        public void Reset()
        {
            State = ScrollState.Normal;
            lastTime = null;
            trigger = ScrollButton.None;
            travel = pendingX = pendingY = 0;
            ResetMotion();
        }

        private void ResetMotion()
        {
            axis = Axis.None;
            motionX = motionY = remainderX = remainderY = 0;
            samples = sampleNext = 0;
            hasRelease = false;
        }

        // Returns the wheel velocity (units per ms) of a scroll released while still moving, once.
        public bool TakeReleaseVelocity(out double x, out double y)
        {
            bool has = hasRelease;
            x = has ? releaseX : 0;
            y = has ? releaseY : 0;
            hasRelease = false;
            return has;
        }

        private void Record(int h, int v)
        {
            sampleTime[sampleNext] = moveTime;
            sampleX[sampleNext] = h;
            sampleY[sampleNext] = v;
            sampleNext = (sampleNext + 1) % SampleCount;
            samples = Math.Min(samples + 1, SampleCount);
        }

        private void CaptureReleaseVelocity(double timeMs)
        {
            hasRelease = false;
            if (samples < 2)
                return;
            int newest = (sampleNext + SampleCount - 1) % SampleCount;
            double newestTime = sampleTime[newest];
            if (timeMs - newestTime > StaleReleaseMs)
                return; // The pointer had stopped before release.

            // Each sample holds the output since the previous sample, so the oldest sample in the
            // window only marks where the measured interval starts.
            double sumX = 0, sumY = 0, oldestTime = newestTime;
            for (int i = 0; i < samples; i++)
            {
                int k = (newest - i + SampleCount) % SampleCount;
                if (newestTime - sampleTime[k] > VelocityWindowMs)
                    break;
                if (i > 0)
                {
                    sumX += sampleX[(k + 1) % SampleCount];
                    sumY += sampleY[(k + 1) % SampleCount];
                }
                oldestTime = sampleTime[k];
            }
            double span = newestTime - oldestTime;
            if (span <= 0)
                return;
            double x = sumX / span, y = sumY / span;
            double speed = Distance(x, y);
            if (speed < MinReleaseVelocity)
                return;
            if (speed > MaxReleaseVelocity)
            {
                x *= MaxReleaseVelocity / speed;
                y *= MaxReleaseVelocity / speed;
            }
            releaseX = x;
            releaseY = y;
            hasRelease = true;
        }

        private ScrollDecision FlushMotion(double dx, double dy)
        {
            ScrollDecision decision = Convert(dx + pendingX, dy + pendingY);
            pendingX = pendingY = 0;
            return decision;
        }

        private ScrollDecision Convert(double dx, double dy)
        {
            if (snapToAxis)
            {
                if (axis == Axis.None)
                    axis = motionX > motionY ? Axis.Horizontal : Axis.Vertical;
                else if (axis == Axis.Horizontal && motionY > 3 * motionX)
                    axis = Axis.Vertical;
                else if (axis == Axis.Vertical && motionX > 3 * motionY)
                    axis = Axis.Horizontal;

                if (axis == Axis.Horizontal)
                    dy = remainderY = 0;
                else
                    dx = remainderX = 0;
            }

            double x = Clamp(dx, -4096, 4096);
            double y = Clamp(dy, -4096, 4096);
            double gain = acceleration ? Math.Min(4, 1 + Distance(x, y) / 100) : 1;
            double scale = speed * gain * (reverse ? -1 : 1);
            double hx = Clamp(x * horizontal * scale + remainderX, -32767, 32767);
            double vy = Clamp(-y * 4 * vertical * scale + remainderY, -32767, 32767);
            int h = (int)hx;
            int v = (int)vy;
            remainderX = hx - h;
            remainderY = vy - v;
            Record(h, v);
            return new ScrollDecision(DecisionKind.Scroll, h, v);
        }

        private static double Distance(double x, double y) { return Math.Sqrt(x * x + y * y); }
        private static bool IsFinite(double x) { return !double.IsNaN(x) && !double.IsInfinity(x); }
        private static double Clamp(double x, double min, double max) { return Math.Max(min, Math.Min(max, x)); }
        private static double Bound(double x, double min, double max, double fallback)
        {
            return IsFinite(x) ? Clamp(x, min, max) : fallback;
        }
    }

    // Exponentially decaying glide in wheel units. Steps integrate the decay exactly, so the
    // distance covered does not depend on how often Step is called.
    internal sealed class Momentum
    {
        internal const double DecayMs = 400;
        internal const double StopVelocity = 0.05; // Wheel units per ms.
        private double velocityX, velocityY, lastTime, remainderX, remainderY;

        public bool Active { get; private set; }

        public void Start(double x, double y, double timeMs)
        {
            bool valid = !double.IsNaN(x) && !double.IsInfinity(x) && !double.IsNaN(y) && !double.IsInfinity(y) &&
                !double.IsNaN(timeMs) && !double.IsInfinity(timeMs);
            velocityX = valid ? x : 0;
            velocityY = valid ? y : 0;
            lastTime = timeMs;
            remainderX = remainderY = 0;
            Active = valid && Math.Sqrt(x * x + y * y) >= StopVelocity;
        }

        public void Stop()
        {
            Active = false;
            velocityX = velocityY = 0;
        }

        public void Step(double timeMs, out int x, out int y)
        {
            x = y = 0;
            if (!Active)
                return;
            double elapsed = timeMs - lastTime;
            if (double.IsNaN(elapsed) || elapsed < 0)
            {
                Stop();
                return;
            }
            lastTime = timeMs;
            double decay = Math.Exp(-elapsed / DecayMs);
            double travel = DecayMs * (1 - decay);
            double hx = velocityX * travel + remainderX;
            double vy = velocityY * travel + remainderY;
            x = (int)hx;
            y = (int)vy;
            remainderX = hx - x;
            remainderY = vy - y;
            velocityX *= decay;
            velocityY *= decay;
            if (Math.Sqrt(velocityX * velocityX + velocityY * velocityY) < StopVelocity)
                Stop();
        }
    }
}
