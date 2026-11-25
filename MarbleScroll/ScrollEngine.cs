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
        public double HoldDurationMs { get; set; } = 300;
        public double? DoubleClickIntervalMs { get; set; }
        public bool Reverse { get; set; }
        public bool Acceleration { get; set; }
        public bool LockEnabled { get; set; }
    }

    internal sealed class ScrollEngine
    {
        private readonly double speed, horizontal, vertical, threshold, holdMs, doubleClickMs;
        private readonly bool acceleration;
        private readonly HashSet<ScrollButton> consumedDowns = new HashSet<ScrollButton>();
        private bool reverse, lockEnabled;
        private double? lastTime;
        private double firstDown, travel, pendingX, pendingY, remainderX, remainderY;
        private ScrollButton trigger;

        public ScrollState State { get; private set; }
        public double? PendingDeadlineMs
        {
            get
            {
                if (State == ScrollState.ButtonPressed)
                    return firstDown + holdMs;
                if (State == ScrollState.WaitingForSecondClick)
                    return firstDown + doubleClickMs + 0.001; // Second down at the boundary still locks.
                return null;
            }
        }

        public ScrollEngine(ScrollSettings settings, double systemDoubleClickMs)
        {
            speed = Bound(settings.Speed, 0.1, 10, 1);
            horizontal = Bound(settings.HorizontalSensitivity, 0, 5, 1);
            vertical = Bound(settings.VerticalSensitivity, 0, 5, 1);
            threshold = Bound(settings.MovementThreshold, 0, 30, 2);
            holdMs = Bound(settings.HoldDurationMs, 50, 1000, 300);
            doubleClickMs = settings.DoubleClickIntervalMs.HasValue
                ? Bound(settings.DoubleClickIntervalMs.Value, 150, 1500, 500)
                : Bound(systemDoubleClickMs, 1, 5000, 500);
            reverse = settings.Reverse;
            acceleration = settings.Acceleration;
            lockEnabled = settings.LockEnabled;
        }

        public bool IsTrigger(ScrollButton button) { return button != ScrollButton.None && trigger == button; }

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
            }
            else
            {
                if (State == ScrollState.WaitingForSecondClick)
                    replay = FinishWaiting(DecisionKind.Pass).ReplayButton;
                State = ScrollState.ButtonPressed;
                trigger = button;
                firstDown = timeMs;
                travel = pendingX = pendingY = 0;
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
                pendingX = pendingY = 0;
                if (State == ScrollState.ButtonPressed)
                {
                    State = ScrollState.WaitingForSecondClick;
                    travel = 0;
                    if (!lockEnabled || timeMs - firstDown > doubleClickMs)
                        return FinishWaiting(DecisionKind.ReplayClick);
                }
                else if (State == ScrollState.TemporaryScroll)
                {
                    State = ScrollState.Normal;
                }
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

            switch (State)
            {
                case ScrollState.WaitingForSecondClick:
                    travel += Distance(dx, dy);
                    if (travel >= threshold)
                        return FinishWaiting(DecisionKind.Pass);
                    break;
                case ScrollState.ButtonPressed:
                    travel += Distance(dx, dy);
                    pendingX += dx;
                    pendingY += dy;
                    if (travel < threshold)
                        return new ScrollDecision(DecisionKind.Suppress);
                    State = ScrollState.TemporaryScroll;
                    return FlushMotion(0, 0);
                case ScrollState.TemporaryScroll:
                    return FlushMotion(dx, dy);
                case ScrollState.LockedScroll:
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
            if (State == ScrollState.ButtonPressed && timeMs - firstDown >= holdMs)
                State = ScrollState.TemporaryScroll;
            if (State == ScrollState.WaitingForSecondClick && timeMs - firstDown > doubleClickMs)
                replay = FinishWaiting(DecisionKind.Pass).ReplayButton;
            return true;
        }

        private ScrollDecision FinishWaiting(DecisionKind kind)
        {
            State = ScrollState.Normal;
            return new ScrollDecision(kind, replayButton: trigger);
        }

        private void Reset()
        {
            State = ScrollState.Normal;
            lastTime = null;
            consumedDowns.Clear();
            trigger = ScrollButton.None;
            travel = pendingX = pendingY = remainderX = remainderY = 0;
        }

        private ScrollDecision FlushMotion(double dx, double dy)
        {
            ScrollDecision decision = Convert(dx + pendingX, dy + pendingY);
            pendingX = pendingY = 0;
            return decision;
        }

        private ScrollDecision Convert(double dx, double dy)
        {
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
}
