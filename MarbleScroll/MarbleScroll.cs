using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Timer = System.Windows.Forms.Timer;

namespace MarbleScroll
{
    public class MarbleScroll
    {
        private const uint InjectedFlag = 0x1;
        private static readonly UIntPtr OutputMarker = new UIntPtr(0x4D534352);
        private static readonly int InputSize = Marshal.SizeOf(typeof(Input));
        private readonly LowLevelMouseProc hookCallback;
        private readonly Stopwatch clock = new Stopwatch();
        private readonly Queue<Output> outputQueue = new Queue<Output>();
        private IntPtr hookID;
        private Timer gestureTimer;
        private double? scheduledDeadline;
        private ScrollEngine engine;
        private POINT frozenPoint;
        private Thread worker;
        private Output queueTail;
        private bool stopping;
        private bool smoothScrolling = true;
        private bool lockEnabled, reverse;
        private int notchRemainderX, notchRemainderY;

        public bool FocusWindow { get; set; }
        public bool ConvertXToMiddleClick { get; set; }
        public bool EnableMiddleButtonScroll { get; set; }
        public bool EnableXButtonScroll { get; set; }
        public uint ScrollXButton { get; set; } = 1;

        public bool SmoothScrolling
        {
            get { return smoothScrolling; }
            set
            {
                if (smoothScrolling != value)
                    notchRemainderX = notchRemainderY = 0;
                smoothScrolling = value;
            }
        }

        public bool LockEnabled
        {
            get { return lockEnabled; }
            set
            {
                lockEnabled = value;
                if (engine != null)
                    ApplyDecision(engine.SetLockEnabled(value));
            }
        }

        public bool Reverse
        {
            get { return reverse; }
            set
            {
                if (reverse != value)
                    notchRemainderX = notchRemainderY = 0;
                reverse = value;
                if (engine != null)
                    engine.SetReverse(value);
            }
        }

        public MarbleScroll()
        {
            hookCallback = HookCallback;
        }

        public void Start()
        {
            if (hookID != IntPtr.Zero)
                return;

            engine = new ScrollEngine(new ScrollSettings { LockEnabled = lockEnabled, Reverse = reverse },
                SystemInformation.DoubleClickTime);
            gestureTimer = new Timer();
            gestureTimer.Tick += OnGestureTimer;
            clock.Restart();

            using (Process process = Process.GetCurrentProcess())
            using (ProcessModule module = process.MainModule)
                hookID = SetWindowsHookEx(14, hookCallback, GetModuleHandle(module.ModuleName), 0);

            if (hookID == IntPtr.Zero)
            {
                int error = Marshal.GetLastWin32Error();
                Stop();
                throw new Win32Exception(error);
            }

            stopping = false;
            worker = new Thread(EmitOutput) { IsBackground = true, Name = "MarbleScroll output" };
            worker.Start();
        }

        public void Stop()
        {
            if (gestureTimer != null)
            {
                gestureTimer.Dispose();
                gestureTimer = null;
            }
            scheduledDeadline = null;
            // SendInput can wait for the hook thread. Remove the hook before joining the worker.
            if (hookID != IntPtr.Zero)
            {
                UnhookWindowsHookEx(hookID);
                hookID = IntPtr.Zero;
            }
            lock (outputQueue)
            {
                stopping = true;
                outputQueue.Clear();
                queueTail = null;
                Monitor.Pulse(outputQueue);
            }
            if (worker != null)
            {
                worker.Join();
                worker = null;
            }
            engine = null;
            notchRemainderX = notchRemainderY = 0;
            clock.Stop();
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode < 0)
                return CallNextHookEx(hookID, nCode, wParam, lParam);

            var mouse = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(MSLLHOOKSTRUCT));
            if ((mouse.flags & InjectedFlag) != 0 && mouse.dwExtraInfo == OutputMarker)
                return CallNextHookEx(hookID, nCode, wParam, lParam);

            double now = clock.Elapsed.TotalMilliseconds;
            var message = (MouseMessage)wParam.ToInt32();
            ScrollDecision decision;
            switch (message)
            {
                case MouseMessage.Move:
                    POINT origin = frozenPoint;
                    if (engine.State == ScrollState.WaitingForSecondClick && !GetCursorPos(out origin))
                        origin = mouse.pt;
                    decision = engine.Move((double)mouse.pt.x - origin.x, (double)mouse.pt.y - origin.y, now);
                    break;
                case MouseMessage.MiddleDown:
                case MouseMessage.XDown:
                    ScrollButton button = GetButton(message, mouse.mouseData);
                    bool enabled = button == ScrollButton.Middle ? EnableMiddleButtonScroll :
                        EnableXButtonScroll && (mouse.mouseData >> 16) == ScrollXButton;
                    // Retain the original trigger as an escape hatch if it was disabled while locked.
                    if (!enabled && !(engine.State == ScrollState.LockedScroll && engine.IsTrigger(button)))
                        button = ScrollButton.None;
                    ScrollState previousState = engine.State;
                    decision = engine.ButtonDown(button, now);
                    if ((engine.State == ScrollState.ButtonPressed || engine.State == ScrollState.LockedScroll) &&
                        previousState != engine.State && engine.IsTrigger(button) && decision.Kind == DecisionKind.Suppress)
                    {
                        // Real downs use the current cursor position even when locking after a little jitter.
                        frozenPoint = mouse.pt;
                        if (FocusWindow)
                        {
                            IntPtr window = GetAncestor(WindowFromPoint(mouse.pt), 2); // GA_ROOT
                            if (window != IntPtr.Zero && window != GetForegroundWindow())
                                SetForegroundWindow(window);
                        }
                    }
                    break;
                case MouseMessage.MiddleUp:
                case MouseMessage.XUp:
                    decision = engine.ButtonUp(GetButton(message, mouse.mouseData), now);
                    break;
                default:
                    ApplyDecision(engine.Tick(now));
                    return CallNextHookEx(hookID, nCode, wParam, lParam);
            }

            ApplyDecision(decision);
            return decision.Kind == DecisionKind.Pass
                ? CallNextHookEx(hookID, nCode, wParam, lParam)
                : new IntPtr(1);
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

        private void OnGestureTimer(object sender, EventArgs e)
        {
            gestureTimer.Stop();
            scheduledDeadline = null;
            ApplyDecision(engine.Tick(clock.Elapsed.TotalMilliseconds));
        }

        private void ApplyDecision(ScrollDecision decision)
        {
            if (decision.ReplayButton != ScrollButton.None)
            {
                ScrollButton button = ConvertXToMiddleClick ? ScrollButton.Middle : decision.ReplayButton;
                Enqueue(new Output { Click = button });
            }

            if (decision.Kind == DecisionKind.Scroll)
            {
                int x = decision.XUnits;
                int y = decision.YUnits;
                if (!smoothScrolling)
                {
                    x = TakeNotches(x, ref notchRemainderX);
                    y = TakeNotches(y, ref notchRemainderY);
                }
                if (x != 0 || y != 0)
                    Enqueue(new Output { X = x, Y = y });
            }

            double? deadline = engine.PendingDeadlineMs;
            if (deadline != scheduledDeadline)
            {
                gestureTimer.Stop();
                scheduledDeadline = deadline;
                if (deadline.HasValue)
                {
                    gestureTimer.Interval = Math.Max(1, (int)Math.Ceiling(deadline.Value - clock.Elapsed.TotalMilliseconds));
                    gestureTimer.Start();
                }
            }
        }

        private static int TakeNotches(int units, ref int remainder)
        {
            int total = units + remainder;
            int whole = total / 120 * 120;
            remainder = total - whole;
            return whole;
        }

        private void Enqueue(Output output)
        {
            lock (outputQueue)
            {
                if (output.Click == ScrollButton.None && queueTail != null &&
                    queueTail.Click == ScrollButton.None &&
                    Math.Abs(queueTail.X + output.X) <= 32767 &&
                    Math.Abs(queueTail.Y + output.Y) <= 32767)
                {
                    queueTail.X += output.X;
                    queueTail.Y += output.Y;
                }
                else
                {
                    outputQueue.Enqueue(output);
                    queueTail = output;
                }
                Monitor.Pulse(outputQueue);
            }
        }

        private void EmitOutput()
        {
            var cadence = Stopwatch.StartNew();
            double nextWheel = 0;
            while (true)
            {
                Output output;
                lock (outputQueue)
                {
                    while (true)
                    {
                        if (stopping)
                            return;
                        if (outputQueue.Count != 0)
                        {
                            if (outputQueue.Peek().Click != ScrollButton.None ||
                                cadence.Elapsed.TotalMilliseconds >= nextWheel)
                                break;
                            Monitor.Wait(outputQueue, Math.Max(1, (int)Math.Ceiling(nextWheel - cadence.Elapsed.TotalMilliseconds)));
                        }
                        else
                        {
                            Monitor.Wait(outputQueue);
                        }
                    }
                    output = outputQueue.Dequeue();
                    if (outputQueue.Count == 0)
                        queueTail = null;
                }

                if (output.Click != ScrollButton.None)
                {
                    bool middle = output.Click == ScrollButton.Middle;
                    int data = middle ? 0 : (output.Click == ScrollButton.X1 ? 1 : 2);
                    Send(MakeInput(middle ? 0x0020u : 0x0080u, data),
                        MakeInput(middle ? 0x0040u : 0x0100u, data));
                }
                else if (output.X != 0 || output.Y != 0)
                {
                    nextWheel = cadence.Elapsed.TotalMilliseconds + 8;
                    if (output.X != 0 && output.Y != 0)
                        Send(MakeInput(0x1000, output.X), MakeInput(0x0800, output.Y));
                    else
                        Send(output.X != 0 ? MakeInput(0x1000, output.X) : MakeInput(0x0800, output.Y));
                }
            }
        }

        private static Input MakeInput(uint flags, int data)
        {
            return new Input { Mouse = new MouseInput { Flags = flags, Data = data, ExtraInfo = OutputMarker } };
        }

        private static void Send(params Input[] inputs)
        {
            uint sent = SendInput((uint)inputs.Length, inputs, InputSize);
            if (sent != inputs.Length)
                Debug.WriteLine("MarbleScroll SendInput inserted " + sent + " of " + inputs.Length + " events.");
            // If only the synthetic down was inserted, attempt to release it.
            if (sent == 1 && inputs.Length == 2 && (inputs[1].Mouse.Flags & (0x0040 | 0x0100)) != 0)
                SendInput(1, new[] { inputs[1] }, InputSize);
        }

        private sealed class Output
        {
            public int X, Y;
            public ScrollButton Click;
        }

        private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

        private enum MouseMessage
        {
            Move = 0x0200,
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
        private static extern bool GetCursorPos(out POINT point);

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
        private static extern bool SetForegroundWindow(IntPtr window);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();
    }
}
