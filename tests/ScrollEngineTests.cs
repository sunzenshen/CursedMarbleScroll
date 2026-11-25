using System;
using MarbleScroll;

internal static class ScrollEngineTests
{
    private const ScrollButton Middle = ScrollButton.Middle;

    private static int Main()
    {
        try
        {
            ThresholdAndHold();
            DominantAxis();
            ResetKeepsButtonOwnership();
            DirectionsAndSensitivity();
            RemaindersAndBounds();
            ClickTiming();
            LockAndUnlock();
            InterclickMovement();
            TimeReset();
            Console.WriteLine("ScrollEngine tests passed.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static void Equal<T>(T expected, T actual, string description)
    {
        if (!Equals(expected, actual))
            throw new InvalidOperationException(description + ": expected " + expected + ", got " + actual);
    }

    private static ScrollEngine Engine()
    {
        return new ScrollEngine(new ScrollSettings());
    }

    private static void Units(ScrollDecision decision, int x, int y, string description)
    {
        Equal(DecisionKind.Scroll, decision.Kind, description);
        Equal(x, decision.XUnits, description + " horizontal");
        Equal(y, decision.YUnits, description + " vertical");
    }

    private static ScrollDecision Click(ScrollEngine engine, double time)
    {
        Equal(DecisionKind.Suppress, engine.ButtonDown(Middle, time).Kind, "trigger down");
        return engine.ButtonUp(Middle, time + 20);
    }

    private static void ThresholdAndHold()
    {
        var engine = Engine();
        Equal(DecisionKind.Pass, engine.Move(5, 5, 0).Kind, "normal pointer");
        engine.ButtonDown(Middle, 1);
        Equal(false, engine.PendingDeadlineMs.HasValue, "no hold deadline");
        Equal(DecisionKind.Suppress, engine.Move(1, 0, 2).Kind, "buffer jitter");
        Units(engine.Move(1, 0, 3), 2, 0, "threshold flushes the buffer");
        Equal(DecisionKind.Suppress, engine.ButtonUp(Middle, 4).Kind, "scroll release");
        Equal(DecisionKind.Pass, engine.Move(2, 2, 5).Kind, "pointer restored");

        engine.ButtonDown(Middle, 10);
        engine.Move(1, 0, 11);
        engine.Tick(310);
        Equal(ScrollState.ButtonPressed, engine.State, "hold alone does not activate scrolling");
        Units(engine.Move(1, 0, 311), 2, 0, "hold keeps the buffer");
        engine.ButtonUp(Middle, 312);
        engine.SetLockEnabled(false);
        engine.ButtonDown(Middle, 400);
        Equal(DecisionKind.ReplayClick, engine.ButtonUp(Middle, 1400).Kind, "long stationary hold replays click");

        engine = Engine();
        engine.ButtonDown(Middle, 0);
        Equal(DecisionKind.ReplayClick, engine.ButtonUp(Middle, 1000).Kind,
            "long stationary hold also replays with lock enabled");
    }

    private static void DominantAxis()
    {
        var engine = new ScrollEngine(new ScrollSettings { SnapToAxis = true });
        engine.ButtonDown(Middle, 0);
        Equal(DecisionKind.Suppress, engine.Move(0.5, 0.5, 1).Kind, "buffer before axis choice");
        Units(engine.Move(0.5, 2.5, 2), 0, -12, "vertical wins from accumulated motion");
        Units(engine.Move(8, 1, 3), 0, -4, "horizontal stays below three times accumulated vertical");
        Units(engine.Move(3, 0, 4), 0, 0, "three times exactly keeps the axis");
        Units(engine.Move(1, 0, 5), 1, 0, "horizontal exceeds three times accumulated vertical");
        engine.ButtonUp(Middle, 6);
        engine.ButtonDown(Middle, 7);
        Units(engine.Move(1, 3, 8), 0, -12, "new gesture chooses its own axis");
    }

    private static void ResetKeepsButtonOwnership()
    {
        var engine = Engine();
        engine.ButtonDown(Middle, 0);
        engine.Move(0, 10, 1);
        engine.Reset();
        Equal(DecisionKind.Pass, engine.Move(0, 10, 2).Kind, "reset restores pointer");
        Equal(DecisionKind.Suppress, engine.ButtonUp(Middle, 3).Kind, "reset still pairs the consumed down");
        Equal(DecisionKind.Pass, engine.ButtonUp(Middle, 4).Kind, "ownership is released");
        engine.SetLockEnabled(false);
        Equal(DecisionKind.ReplayClick, Click(engine, 5).Kind, "next stationary click replays");
    }

    private static void DirectionsAndSensitivity()
    {
        var engine = Engine();
        engine.ButtonDown(Middle, 0);
        Units(engine.Move(3, 4, 1), 3, -16, "right and down");
        Units(engine.Move(-3, -4, 2), -3, 16, "left and up");
        engine.SetReverse(true);
        Units(engine.Move(3, 4, 3), -3, 16, "reverse both axes");

        var settings = new ScrollSettings { HorizontalSensitivity = 0.5, VerticalSensitivity = 2 };
        engine = new ScrollEngine(settings);
        engine.ButtonDown(Middle, 0);
        Units(engine.Move(4, 3, 1), 2, -24, "independent sensitivity");
    }

    private static void RemaindersAndBounds()
    {
        var settings = new ScrollSettings { Speed = 0.25, MovementThreshold = 0 };
        var engine = new ScrollEngine(settings);
        engine.ButtonDown(Middle, 0);
        int x = 0, y = 0;
        for (int i = 1; i <= 100; i++)
        {
            ScrollDecision move = engine.Move(1, 0.25, i);
            x += move.XUnits;
            y += move.YUnits;
        }
        Equal(25, x, "horizontal remainder");
        Equal(-25, y, "vertical remainder");
        engine.Move(3, 0.75, 101);
        engine.ButtonUp(Middle, 102);
        engine.ButtonDown(Middle, 103);
        Units(engine.Move(1, 0.25, 104), 0, 0, "fractional units do not carry into the next gesture");

        settings = new ScrollSettings { Acceleration = true };
        engine = new ScrollEngine(settings);
        engine.ButtonDown(Middle, 0);
        Units(engine.Move(100, 0, 1), 200, 0, "acceleration");
        Units(engine.Move(double.MaxValue, double.MaxValue, 2), 16384, -32767, "input, gain and output clamps");
        Equal(DecisionKind.Pass, engine.Move(double.NaN, 0, 3).Kind, "invalid movement fails open");
        Equal(ScrollState.Normal, engine.State, "invalid movement resets");

        settings = new ScrollSettings { Speed = double.NaN, HorizontalSensitivity = 99, VerticalSensitivity = -1 };
        engine = new ScrollEngine(settings);
        engine.ButtonDown(Middle, 0);
        Units(engine.Move(4, 3, 1), 20, 0, "settings bounds and fallback");
    }

    private static void ClickTiming()
    {
        var engine = Engine();
        engine.SetLockEnabled(false);
        var immediate = Click(engine, 0);
        Equal(DecisionKind.ReplayClick, immediate.Kind, "lock off replays on release");
        Equal(Middle, immediate.ReplayButton, "immediate replay button");
        Equal(false, engine.PendingDeadlineMs.HasValue, "no replay timer with lock off");
        Equal(DecisionKind.ReplayClick, Click(engine, 100).Kind, "quick second click also replays");

        engine = Engine();
        Equal(DecisionKind.Suppress, Click(engine, 0).Kind, "lock on holds first click");
        double deadline = engine.PendingDeadlineMs.Value;
        Equal(DecisionKind.Pass, engine.Tick(300).Kind, "inclusive double-click boundary");
        var late = engine.Tick(deadline);
        Equal(DecisionKind.ReplayClick, late.Kind, "single click replays at deadline");
        Equal(Middle, late.ReplayButton, "delayed replay button");
        Equal(DecisionKind.Pass, engine.Tick(600).Kind, "no duplicate replay");

        Click(engine, 700);
        var newPress = engine.ButtonDown(Middle, 1001);
        Equal(DecisionKind.Suppress, newPress.Kind, "expired first click permits new press");
        Equal(Middle, newPress.ReplayButton, "expired click replays even if timer has not run");
        Equal(ScrollState.ButtonPressed, engine.State, "late second press starts new gesture");

        engine = new ScrollEngine(new ScrollSettings { LockWindowMs = 100 });
        Click(engine, 0);
        Equal(DecisionKind.Pass, engine.Tick(150).Kind, "minimum window is inclusive");
        Equal(DecisionKind.ReplayClick, engine.Tick(151).Kind, "window clamps to 150 ms");
        engine.ButtonDown(ScrollButton.X1, 200);
        Equal(ScrollButton.X1, engine.ButtonUp(ScrollButton.X1, 351).ReplayButton,
            "side button released after the window replays immediately");

        engine = new ScrollEngine(new ScrollSettings { LockWindowMs = 1500 });
        Click(engine, 0);
        Equal(DecisionKind.Pass, engine.Tick(1000).Kind, "maximum window is inclusive");
        Equal(DecisionKind.ReplayClick, engine.Tick(1001).Kind, "window clamps to 1000 ms");
    }

    private static void LockAndUnlock()
    {
        var engine = Engine();
        Click(engine, 0);
        Equal(DecisionKind.Suppress, engine.ButtonDown(Middle, 300).Kind, "second press reserved");
        Equal(ScrollState.LockedScroll, engine.State, "locks at inclusive boundary");
        engine.ButtonDown(Middle, 301);
        Equal(ScrollState.LockedScroll, engine.State, "duplicate activation down ignored");
        Equal(DecisionKind.Suppress, engine.ButtonUp(Middle, 302).Kind, "activation release reserved");
        Units(engine.Move(3, 2, 303), 3, -8, "scrolls without holding");
        Equal(DecisionKind.Pass, engine.ButtonDown(ScrollButton.None, 304).Kind, "unrelated down passes");
        Equal(DecisionKind.Pass, engine.ButtonUp(ScrollButton.X2, 305).Kind, "unmatched up passes");
        Equal(ScrollState.LockedScroll, engine.State, "other buttons preserve lock");
        Equal(DecisionKind.Suppress, engine.ButtonDown(Middle, 306).Kind, "unlock down reserved");
        Equal(DecisionKind.Pass, engine.Move(3, 2, 307).Kind, "moves while unlock press is held");
        Equal(DecisionKind.Suppress, engine.ButtonUp(Middle, 308).Kind, "unlock release reserved");
        Equal(ScrollState.Normal, engine.State, "unlock restored normal mode");
    }

    private static void InterclickMovement()
    {
        var engine = Engine();
        Click(engine, 0);
        var jitter = engine.Move(0.5, 0, 30);
        Equal(DecisionKind.Pass, jitter.Kind, "jitter passes");
        Equal(ScrollButton.None, jitter.ReplayButton, "jitter preserves pending click");
        var move = engine.Move(1.5, 0, 31);
        Equal(DecisionKind.Pass, move.Kind, "motion after click passes");
        Equal(Middle, move.ReplayButton, "motion cancels lock recognition and replays click");
        engine.ButtonDown(Middle, 32);
        Equal(ScrollState.ButtonPressed, engine.State, "next press starts a new click");
    }

    private static void TimeReset()
    {
        var engine = Engine();
        Click(engine, 100);
        Click(engine, 150);
        Equal(ScrollState.LockedScroll, engine.State, "locked before time reset");
        Equal(DecisionKind.Pass, engine.Move(2, 2, 1).Kind, "backwards time fails open");
        Equal(ScrollState.Normal, engine.State, "backwards time resets lock");
        Equal(false, engine.PendingDeadlineMs.HasValue, "backwards time clears deadline");
        Equal(DecisionKind.Suppress, Click(engine, 2).Kind, "new timeline accepts clicks");
    }
}
