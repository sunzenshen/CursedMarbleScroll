using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Timer = System.Windows.Forms.Timer;

namespace MarbleScroll
{
    internal sealed class MarbleScroll
    {
        private const uint InjectedFlag = 1;
        private const int QueueLimit = 64;
        private const int HookStartupTimeoutMs = 5000;
        private static readonly UIntPtr OutputMarker = new UIntPtr(
            unchecked(0x4D534352u ^ (uint)Process.GetCurrentProcess().Id));
        private static readonly int InputSize = Marshal.SizeOf(typeof(Input));
        private readonly LowLevelMouseProc hookCallback;
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly Queue<Output> outputQueue = new Queue<Output>(QueueLimit);
        private readonly int lockIdleMs;
        private readonly Thread hookThread, worker;
        private readonly ManualResetEventSlim ready = new ManualResetEventSlim();
        private Exception startupError;
        private volatile Exception hookFailure;
        private volatile Control dispatcher;
        private volatile bool stopping, outputFailed, senderUnavailable, gliding;
        private volatile int forwardedButtons;
        private volatile ScrollState state;
        private IntPtr hookID;
        private ScrollEngine engine;
        private ScrollOptions options;
        private POINT frozenPoint, lastPoint;
        private double lastInput, lastLockMotion;
        private int notchRemainderX, notchRemainderY;
        private ClickContext activeClick, waitingClick, idleClick;
        private ScrollButton idleButton;
        private Output queueTail;
        private int generation;
        private bool releaseRequested;
        // Only the sender thread owns this release, including across gesture resets.
        private Input[] outstandingRelease;

        public MarbleScroll(ScrollOptions options, int lockIdleMs)
        {
            this.options = options.Copy();
            this.lockIdleMs = lockIdleMs;
            hookCallback = HookCallback;
            hookThread = new Thread(RunHook) { IsBackground = true, Name = "MarbleScroll hook" };
            hookThread.SetApartmentState(ApartmentState.STA);
            worker = new Thread(EmitOutput) { IsBackground = true, Name = "MarbleScroll output" };
        }

        public event Action HookFailed;

        public Exception HookFailure { get { return hookFailure; } }

        public ScrollState State { get { return state; } }

        public static bool CheckNativeLayouts(int expectedBits)
        {
            return IntPtr.Size * 8 == expectedBits &&
                InputSize == (IntPtr.Size == 8 ? 40 : 28) &&
                Marshal.SizeOf(typeof(MSLLHOOKSTRUCT)) == (IntPtr.Size == 8 ? 32 : 24);
        }

        public void Start()
        {
            try
            {
                worker.Start();
                hookThread.Start();
                if (!ready.Wait(HookStartupTimeoutMs))
                    throw new TimeoutException("Timed out while installing the mouse hook.");
                if (startupError != null)
                    throw new InvalidOperationException("Could not start the mouse hook.", startupError);
            }
            catch
            {
                Stop();
                throw;
            }
        }

        public void Stop()
        {
            stopping = true;
            InvalidateOutput();
            var timeout = Stopwatch.StartNew();
            if ((hookThread.ThreadState & System.Threading.ThreadState.Unstarted) == 0 && !hookThread.Join(1000))
                Debug.WriteLine("MarbleScroll hook shutdown timed out.");
            int remaining = Math.Max(0, 1000 - (int)timeout.ElapsedMilliseconds);
            if ((worker.ThreadState & System.Threading.ThreadState.Unstarted) == 0 && !worker.Join(remaining))
                Debug.WriteLine("MarbleScroll output shutdown timed out.");
        }

        public void Reset(bool reinstall)
        {
            Post(() =>
            {
                ResetGesture();
                if (reinstall)
                {
                    try
                    {
                        RemoveHook();
                        forwardedButtons = 0;
                        InstallHook();
                    }
                    catch (Exception error)
                    {
                        ReportHookFailure(new InvalidOperationException("Could not reinstall the mouse hook.", error));
                    }
                }
            });
        }

        public void UpdateOptions(ScrollOptions settings)
        {
            ScrollOptions snapshot = settings.Copy();
            Post(() =>
            {
                bool cancelGesture = options.Paused != snapshot.Paused ||
                    (options.SideButtonsActAsMiddle && !snapshot.SideButtonsActAsMiddle &&
                        (engine.IsTrigger(ScrollButton.X1) || engine.IsTrigger(ScrollButton.X2) ||
                        idleButton == ScrollButton.X1 || idleButton == ScrollButton.X2)) ||
                    (engine.State == ScrollState.LockedScroll && !snapshot.LockEnabled);
                bool outputChanged = options.Speed != snapshot.Speed || options.Reverse != snapshot.Reverse ||
                    options.SmoothScrolling != snapshot.SmoothScrolling || options.SnapToAxis != snapshot.SnapToAxis ||
                    options.Momentum != snapshot.Momentum;
                options = snapshot;
                if (cancelGesture)
                    ResetGesture();
                else if (outputChanged)
                    InvalidateOutput();
                engine.SetMotionOptions(options.Speed, options.SnapToAxis);
                engine.SetReverse(options.Reverse);
                if (outputChanged)
                    notchRemainderX = notchRemainderY = 0;
                ApplyDecision(engine.SetLockEnabled(options.LockEnabled), waitingClick);
                if (engine.State != ScrollState.WaitingForSecondClick)
                    waitingClick = default(ClickContext);
                state = engine.State;
            });
        }

        private void Post(Action action)
        {
            Control control = dispatcher;
            if (stopping || control == null)
                return;
            try
            {
                control.BeginInvoke((MethodInvoker)(() =>
                {
                    if (!stopping)
                        Guard(action);
                }));
            }
            catch (InvalidOperationException error)
            {
                Debug.WriteLine(error);
            }
        }

        private void RunHook()
        {
            bool started = false;
            try
            {
                engine = new ScrollEngine(new ScrollSettings
                {
                    LockEnabled = options.LockEnabled,
                    LockWindowMs = options.LockWindowMs,
                    Reverse = options.Reverse,
                    Speed = options.Speed,
                    SnapToAxis = options.SnapToAxis
                });
                if (stopping)
                    throw new OperationCanceledException("Mouse hook startup was canceled.");
                PrepareHookPath();
                // Install before WinForms creates the hook thread's marshaling window.
                InstallHook();

                using (var control = new Control())
                using (var context = new ApplicationContext())
                using (var timer = new Timer { Interval = 20 })
                {
                    control.CreateControl();
                    timer.Tick += (sender, args) =>
                    {
                        Guard(() =>
                        {
                            if (stopping)
                            {
                                context.ExitThread();
                            }
                            else
                            {
                                AdvanceGesture(clock.Elapsed.TotalMilliseconds);
                            }
                        });
                    };
                    timer.Start();
                    dispatcher = control;
                    started = true;
                    ready.Set();
                    if (!stopping)
                        Application.Run(context);
                }
            }
            catch (Exception error)
            {
                if (started)
                    ReportHookFailure(error);
                else
                    startupError = error;
            }
            finally
            {
                dispatcher = null;
                try
                {
                    RemoveHook();
                }
                catch (Win32Exception error)
                {
                    Debug.WriteLine(error);
                }
                if (!started)
                    ready.Set();
            }
        }

        // Compile the callback path before Windows waits on it, so the first event after launch does not stall.
        private static void PrepareHookPath()
        {
            try
            {
                const BindingFlags declared = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public |
                    BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
                foreach (Type type in new[] { typeof(MarbleScroll), typeof(ScrollEngine) })
                {
                    foreach (MethodInfo method in type.GetMethods(declared))
                    {
                        if (!method.IsAbstract && !method.ContainsGenericParameters &&
                            (method.Attributes & MethodAttributes.PinvokeImpl) == 0)
                            RuntimeHelpers.PrepareMethod(method.MethodHandle);
                    }
                }
                Marshal.PrelinkAll(typeof(MarbleScroll));

                // Run generic collection code that the framework does not precompile for these types.
                var probe = new ScrollEngine(new ScrollSettings());
                probe.ButtonDown(ScrollButton.Middle, 0);
                probe.Move(3, 3, 1);
                probe.ButtonUp(ScrollButton.Middle, 2);

                IntPtr buffer = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(MSLLHOOKSTRUCT)));
                try
                {
                    Marshal.PtrToStructure(buffer, typeof(MSLLHOOKSTRUCT));
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
                RootAt(default(POINT));
            }
            catch (Exception error)
            {
                // Warm-up is only an optimization.
                Debug.WriteLine(error);
            }
        }

        private void ReportHookFailure(Exception error)
        {
            if (stopping)
                return;
            hookFailure = error;
            stopping = true;
            InvalidateOutput();
            HookFailed?.Invoke();
        }

        private void InstallHook()
        {
            hookID = SetWindowsHookEx(14, hookCallback, GetModuleHandle(null), 0);
            if (hookID == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        private void RemoveHook()
        {
            if (hookID == IntPtr.Zero)
                return;
            if (!UnhookWindowsHookEx(hookID))
            {
                int error = Marshal.GetLastWin32Error();
                // ERROR_INVALID_HOOK_HANDLE also covers removal by the system.
                if (error != 1404)
                    throw new Win32Exception(error);
            }
            hookID = IntPtr.Zero;
        }

        private void Guard(Action action)
        {
            try
            {
                action();
            }
            catch (Exception error)
            {
                Debug.WriteLine(error);
                ResetGesture();
            }
        }

        private void ResetGesture()
        {
            engine?.Reset();
            state = ScrollState.Normal;
            activeClick = waitingClick = idleClick = default(ClickContext);
            idleButton = ScrollButton.None;
            notchRemainderX = notchRemainderY = 0;
            InvalidateOutput();
        }

        private void AdvanceGesture(double now)
        {
            if (outputFailed)
            {
                outputFailed = false;
                ResetGesture();
            }
            if ((engine.State == ScrollState.ButtonPressed || engine.State == ScrollState.TemporaryScroll) &&
                now - lastInput >= 30000)
            {
                // Stop suppressing motion but preserve a stationary click's eventual release.
                ScrollButton button = engine.State == ScrollState.ButtonPressed ? activeClick.Button : ScrollButton.None;
                ClickContext click = activeClick;
                ResetGesture();
                idleButton = button;
                idleClick = click;
            }
            else if (engine.State == ScrollState.LockedScroll && now - lastLockMotion >= lockIdleMs)
            {
                ResetGesture();
            }
            if (engine.State == ScrollState.WaitingForSecondClick && now >= engine.PendingDeadlineMs)
            {
                ApplyDecision(engine.Tick(now), waitingClick);
                waitingClick = default(ClickContext);
                state = engine.State;
            }
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            MouseMessage message = 0;
            uint data = 0;
            try
            {
                if (nCode >= 0 && !stopping)
                {
                    var mouse = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(MSLLHOOKSTRUCT));
                    // Other injectors use the same gesture policy as physical input.
                    if ((mouse.flags & InjectedFlag) == 0 || mouse.dwExtraInfo != OutputMarker)
                    {
                        message = (MouseMessage)wParam.ToInt32();
                        data = mouse.mouseData;
                        if (HandleMouse(message, mouse))
                            return new IntPtr(1);
                        TrackForwardedButton(message, mouse.mouseData);
                    }
                }
            }
            catch (Exception error)
            {
                Debug.WriteLine(error);
                ResetGesture();
                if (message == MouseMessage.MiddleDown || message == MouseMessage.MiddleUp ||
                    message == MouseMessage.XDown || message == MouseMessage.XUp)
                {
                    // This event will pass through, so relinquish any claim on its button.
                    engine.ForgetDown(GetButton(message, data));
                    TrackForwardedButton(message, data);
                }
            }
            return CallNextHookEx(hookID, nCode, wParam, lParam);
        }

        private bool HandleMouse(MouseMessage message, MSLLHOOKSTRUCT mouse)
        {
            double now = clock.Elapsed.TotalMilliseconds;
            if (gliding && message != MouseMessage.Move)
                InvalidateOutput(); // Any click or physical wheel turn stops momentum.
            AdvanceGesture(now);
            lastInput = now;
            ScrollState before = engine.State;
            ScrollDecision decision;
            ClickContext replay = waitingClick;
            bool holdPointer = false;
            Output glide = null;
            switch (message)
            {
                case MouseMessage.Move:
                    if (mouse.pt.x != lastPoint.x || mouse.pt.y != lastPoint.y)
                        idleButton = ScrollButton.None;
                    if (options.Paused || before == ScrollState.Normal)
                    {
                        lastPoint = mouse.pt;
                        return false;
                    }
                    POINT origin = before == ScrollState.WaitingForSecondClick ? lastPoint : frozenPoint;
                    double dx = (double)mouse.pt.x - origin.x;
                    double dy = (double)mouse.pt.y - origin.y;
                    if (before == ScrollState.LockedScroll && (dx != 0 || dy != 0))
                        lastLockMotion = now;
                    decision = engine.Move(dx, dy, now);
                    if (decision.ReplayButton != ScrollButton.None && replay.Delayed &&
                        RootAt(mouse.pt) != replay.Root)
                        replay = default(ClickContext);
                    // Motion that ends the lock wait would otherwise move the replayed click off its target.
                    holdPointer = before == ScrollState.WaitingForSecondClick &&
                        decision.ReplayButton != ScrollButton.None && replay.Button != ScrollButton.None;
                    break;
                case MouseMessage.MiddleDown:
                case MouseMessage.XDown:
                    ScrollButton button = GetButton(message, mouse.mouseData);
                    if ((before == ScrollState.ButtonPressed || before == ScrollState.TemporaryScroll) &&
                        engine.IsTrigger(button) && (mouse.flags & InjectedFlag) == 0)
                    {
                        // Mouse buttons do not auto-repeat. A second hardware down can recover a missed up.
                        ResetGesture();
                        before = ScrollState.Normal;
                    }
                    else if (before == ScrollState.LockedScroll && engine.Owns(button) &&
                        (mouse.flags & InjectedFlag) == 0)
                    {
                        // The locking click's release was missed. Let this down unlock instead of being swallowed.
                        engine.ForgetDown(button);
                    }
                    if (before == ScrollState.Normal)
                    {
                        // A fresh down after recovery means its previous release was missed.
                        engine.ForgetDown(button);
                        if (idleButton == button)
                            idleButton = ScrollButton.None;
                    }
                    if (options.Paused || senderUnavailable ||
                        (forwardedButtons & ButtonMask(button)) != 0)
                        return false;
                    bool enabled = button == ScrollButton.Middle ? options.EnableMiddleButtonScroll :
                        (button == ScrollButton.X1 || button == ScrollButton.X2) && options.SideButtonsActAsMiddle;
                    if (!enabled && !(before == ScrollState.LockedScroll && engine.IsTrigger(button)))
                        button = ScrollButton.None;
                    if (before == ScrollState.WaitingForSecondClick && engine.IsTrigger(button) &&
                        RootAt(mouse.pt) != waitingClick.Root)
                    {
                        ResetGesture();
                        before = ScrollState.Normal;
                        replay = default(ClickContext);
                    }
                    decision = engine.ButtonDown(button, now);
                    if (before == ScrollState.LockedScroll && engine.State == ScrollState.Normal)
                        ResetGesture();
                    if (engine.State != before && (engine.State == ScrollState.ButtonPressed ||
                        engine.State == ScrollState.LockedScroll))
                    {
                        notchRemainderX = notchRemainderY = 0;
                        frozenPoint = mouse.pt;
                        lastLockMotion = now;
                        if (engine.State == ScrollState.ButtonPressed)
                        {
                            activeClick = new ClickContext
                            {
                                Button = button,
                                Root = options.LockEnabled ? RootAt(mouse.pt) : IntPtr.Zero,
                                ConsumedAt = now
                            };
                        }
                    }
                    break;
                case MouseMessage.MiddleUp:
                case MouseMessage.XUp:
                    ScrollButton released = GetButton(message, mouse.mouseData);
                    if (!engine.Owns(released))
                        return false;
                    decision = engine.ButtonUp(released, now);
                    if (engine.TakeReleaseVelocity(out double velocityX, out double velocityY) &&
                        before == ScrollState.TemporaryScroll && options.Momentum && options.SmoothScrolling)
                    {
                        glide = new Output
                        {
                            Momentum = true,
                            VelocityX = velocityX,
                            VelocityY = velocityY,
                            Root = RootAt(mouse.pt)
                        };
                    }
                    if (released == idleButton)
                    {
                        replay = idleClick;
                        idleButton = ScrollButton.None;
                        decision = new ScrollDecision(DecisionKind.ReplayClick, replayButton: released);
                    }
                    else if (before == ScrollState.ButtonPressed && engine.IsTrigger(released))
                    {
                        replay = activeClick;
                        if (engine.State == ScrollState.WaitingForSecondClick && activeClick.Root != IntPtr.Zero)
                        {
                            replay.Delayed = true;
                            waitingClick = replay;
                        }
                        else if (engine.State == ScrollState.WaitingForSecondClick)
                        {
                            // Without a known target, skip the double-click wait.
                            decision = engine.SetLockEnabled(false);
                            engine.SetLockEnabled(options.LockEnabled);
                        }
                    }
                    break;
                default:
                    return false;
            }

            lastPoint = mouse.pt;
            if (engine.State != ScrollState.WaitingForSecondClick)
                waitingClick = default(ClickContext);
            state = engine.State;
            ApplyDecision(decision, replay);
            if (glide != null)
                Enqueue(glide); // After the gesture's final deltas.
            if (message == MouseMessage.Move && outputFailed)
            {
                AdvanceGesture(now);
                return false;
            }
            return decision.Kind != DecisionKind.Pass || holdPointer;
        }

        private static int ButtonMask(ScrollButton button)
        {
            return button == ScrollButton.None ? 0 : 1 << (int)button;
        }

        private void TrackForwardedButton(MouseMessage message, uint data)
        {
            if (message == MouseMessage.MiddleDown || message == MouseMessage.XDown)
                forwardedButtons |= ButtonMask(GetButton(message, data));
            else if (message == MouseMessage.MiddleUp || message == MouseMessage.XUp)
                forwardedButtons &= ~ButtonMask(GetButton(message, data));
        }

        private static ScrollButton GetButton(MouseMessage message, uint mouseData)
        {
            if (message == MouseMessage.MiddleDown || message == MouseMessage.MiddleUp)
                return ScrollButton.Middle;
            switch (mouseData >> 16)
            {
                case 1: return ScrollButton.X1;
                case 2: return ScrollButton.X2;
                default: return ScrollButton.None;
            }
        }

        private void ApplyDecision(ScrollDecision decision, ClickContext replay)
        {
            if (decision.ReplayButton != ScrollButton.None && replay.Button != ScrollButton.None)
                Enqueue(new Output { Click = replay });

            if (decision.Kind == DecisionKind.Scroll)
            {
                int x = decision.XUnits;
                int y = decision.YUnits;
                if (!options.SmoothScrolling)
                {
                    x = TakeNotches(x, ref notchRemainderX);
                    y = TakeNotches(y, ref notchRemainderY);
                }
                if (x != 0 || y != 0)
                    Enqueue(new Output { X = x, Y = y });
            }
        }

        private static int TakeNotches(int units, ref int remainder)
        {
            int total = units + remainder;
            int whole = total / 120 * 120;
            remainder = total - whole;
            return whole;
        }

        private void InvalidateOutput()
        {
            lock (outputQueue)
                InvalidateOutputLocked();
        }

        private void InvalidateOutputLocked()
        {
            Interlocked.Increment(ref generation);
            outputQueue.Clear();
            queueTail = null;
            gliding = false; // Any queued or running glide belongs to the old generation.
            releaseRequested = true;
            Monitor.Pulse(outputQueue);
        }

        private void Enqueue(Output output)
        {
            lock (outputQueue)
            {
                if (stopping || outputFailed)
                    return;
                output.Generation = generation;
                if (output.Click.Button == ScrollButton.None && queueTail != null &&
                    queueTail.Click.Button == ScrollButton.None && !output.Momentum && !queueTail.Momentum &&
                    Math.Abs(queueTail.X + output.X) <= 32767 &&
                    Math.Abs(queueTail.Y + output.Y) <= 32767)
                {
                    queueTail.X += output.X;
                    queueTail.Y += output.Y;
                }
                else if (outputQueue.Count < QueueLimit)
                {
                    outputQueue.Enqueue(output);
                    queueTail = output;
                    // Set while queued so input that arrives before the glide starts still cancels it.
                    if (output.Momentum)
                        gliding = true;
                }
                else
                {
                    outputFailed = true;
                    InvalidateOutputLocked();
                }
                Monitor.Pulse(outputQueue);
            }
        }

        private void EmitOutput()
        {
            double nextWheel = 0;
            var momentum = new Momentum();
            int momentumGeneration = 0;
            IntPtr momentumRoot = IntPtr.Zero;
            try
            {
                while (true)
                {
                    Output output = null;
                    bool step = false;
                    lock (outputQueue)
                    {
                        while (!stopping && !releaseRequested)
                        {
                            if (momentum.Active && momentumGeneration != generation)
                                momentum.Stop(); // Invalidation already cleared the flag.
                            if (outputQueue.Count == 0)
                            {
                                if (!momentum.Active)
                                {
                                    Monitor.Wait(outputQueue);
                                    continue;
                                }
                                double wait = nextWheel - clock.Elapsed.TotalMilliseconds;
                                if (wait <= 0)
                                {
                                    step = true;
                                    break;
                                }
                                Monitor.Wait(outputQueue, (int)Math.Ceiling(wait));
                                continue;
                            }
                            double delay = nextWheel - clock.Elapsed.TotalMilliseconds;
                            if (outputQueue.Peek().Click.Button != ScrollButton.None || delay <= 0)
                            {
                                output = outputQueue.Dequeue();
                                if (outputQueue.Count == 0)
                                    queueTail = null;
                                break;
                            }
                            Monitor.Wait(outputQueue, (int)Math.Ceiling(delay));
                        }
                        releaseRequested = false;
                    }

                    if (outstandingRelease != null)
                        ReleaseSyntheticButton();
                    if (stopping)
                        return;
                    if (step)
                    {
                        double now = clock.Elapsed.TotalMilliseconds;
                        // Wheel input goes to the window under the pointer, so stop if that changes.
                        if (momentumGeneration != Volatile.Read(ref generation) ||
                            !GetPhysicalCursorPos(out POINT point) || RootAt(point) != momentumRoot)
                        {
                            EndGlide(momentum, momentumGeneration);
                            continue;
                        }
                        momentum.Step(now, out int glideX, out int glideY);
                        nextWheel = now + 8;
                        EmitWheel(new Output { X = glideX, Y = glideY, Generation = momentumGeneration });
                        if (!momentum.Active)
                            EndGlide(momentum, momentumGeneration);
                        continue;
                    }
                    if (output == null || !IsCurrent(output))
                        continue;

                    if (output.Momentum)
                    {
                        momentum.Start(output.VelocityX, output.VelocityY, clock.Elapsed.TotalMilliseconds);
                        momentumGeneration = output.Generation;
                        momentumRoot = output.Root;
                        if (!momentum.Active)
                            EndGlide(momentum, momentumGeneration);
                        continue;
                    }
                    if (output.Click.Button != ScrollButton.None)
                        EmitClick(output);
                    else
                    {
                        nextWheel = clock.Elapsed.TotalMilliseconds + 8;
                        EmitWheel(output);
                    }
                }
            }
            catch (Exception error)
            {
                Debug.WriteLine(error);
                senderUnavailable = true;
                lock (outputQueue)
                {
                    outputFailed = true;
                    InvalidateOutputLocked();
                }
            }
            finally
            {
                gliding = false;
                try
                {
                    if (outstandingRelease != null)
                        ReleaseSyntheticButton();
                }
                catch (Exception error)
                {
                    Debug.WriteLine(error);
                }
            }
        }

        private void EndGlide(Momentum momentum, int glideGeneration)
        {
            momentum.Stop();
            lock (outputQueue)
            {
                // A newer generation owns the flag, possibly for a glide that is already queued.
                if (glideGeneration == generation)
                    gliding = false;
            }
        }

        private bool IsCurrent(Output output)
        {
            return !stopping && output.Generation == Volatile.Read(ref generation);
        }

        private void EmitClick(Output output)
        {
            ClickContext click = output.Click;
            if (outstandingRelease != null)
                return;
            if (click.Delayed && (clock.Elapsed.TotalMilliseconds - click.ConsumedAt > options.LockWindowMs + 250 ||
                !GetPhysicalCursorPos(out POINT point) || RootAt(point) != click.Root))
                return;
            if ((forwardedButtons & ButtonMask(ScrollButton.Middle)) != 0 ||
                (click.Button != ScrollButton.Middle && (GetAsyncKeyState(4) & 0x8000) != 0) ||
                !IsCurrent(output))
                return;

            Input up = MakeInput(0x0040, 0);
            Input[] release = { up };
            uint sent = Send(MakeInput(0x0020, 0), up);
            if (sent == 1)
            {
                outstandingRelease = release;
                ReleaseSyntheticButton();
            }
            if (sent == 0 || outstandingRelease != null)
                FailOutput(output);
        }

        private void ReleaseSyntheticButton()
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                if (Send(outstandingRelease) == 1)
                {
                    outstandingRelease = null;
                    return;
                }
            }
        }

        private void EmitWheel(Output output)
        {
            if ((output.X == 0 && output.Y == 0) || !IsCurrent(output))
                return;
            bool success = output.X != 0 && output.Y != 0
                ? Send(MakeInput(0x1000, output.X), MakeInput(0x0800, output.Y)) == 2
                : Send(output.X != 0 ? MakeInput(0x1000, output.X) : MakeInput(0x0800, output.Y)) == 1;
            if (!success)
                FailOutput(output);
        }

        private void FailOutput(Output output)
        {
            lock (outputQueue)
            {
                if (output.Generation != generation)
                    return;
                outputFailed = true;
                InvalidateOutputLocked();
            }
        }

        private static IntPtr RootAt(POINT point)
        {
            return GetAncestor(WindowFromPoint(point), 2); // GA_ROOT
        }

        private static Input MakeInput(uint flags, int data)
        {
            return new Input { Mouse = new MouseInput { Flags = flags, Data = data, ExtraInfo = OutputMarker } };
        }

        private static uint Send(params Input[] inputs)
        {
            uint sent = SendInput((uint)inputs.Length, inputs, InputSize);
            if (sent != inputs.Length)
                Debug.WriteLine("MarbleScroll SendInput inserted " + sent + " of " + inputs.Length + " events.");
            return sent;
        }

        private struct ClickContext
        {
            public ScrollButton Button;
            public IntPtr Root;
            public double ConsumedAt;
            public bool Delayed;
        }

        private sealed class Output
        {
            public int X, Y, Generation;
            public ClickContext Click;
            // A momentum start carries the release velocity and the window that was being scrolled.
            public bool Momentum;
            public double VelocityX, VelocityY;
            public IntPtr Root;
        }

        private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

        private enum MouseMessage
        {
            Move = 0x0200,
            LeftDown = 0x0201,
            RightDown = 0x0204,
            Wheel = 0x020A,
            HorizontalWheel = 0x020E,
            MiddleDown = 0x0207,
            MiddleUp = 0x0208,
            XDown = 0x020B,
            XUp = 0x020C
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int x, y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSLLHOOKSTRUCT
        {
            public POINT pt;
            public uint mouseData, flags, time;
            public UIntPtr dwExtraInfo;
        }

        // MOUSEINPUT is the largest member of the native INPUT union.
        [StructLayout(LayoutKind.Sequential)]
        private struct Input
        {
            public uint Type;
            public MouseInput Mouse;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MouseInput
        {
            public int X, Y, Data;
            public uint Flags, Time;
            public UIntPtr ExtraInfo;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint count, [In] Input[] inputs, int size);

        [DllImport("user32.dll")]
        private static extern bool GetPhysicalCursorPos(out POINT point);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc proc, IntPtr module, uint threadId);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnhookWindowsHookEx(IntPtr hook);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hook, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string module);

        [DllImport("user32.dll")]
        private static extern IntPtr WindowFromPoint(POINT point);

        [DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr window, uint flags);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int key);
    }
}
