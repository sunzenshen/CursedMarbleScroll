param(
    [Parameter(Mandatory = $true)]
    [string]$Exe
)

$ErrorActionPreference = 'Stop'

Add-Type -ReferencedAssemblies System.Windows.Forms.dll, System.Drawing.dll -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

public sealed class MouseTestDesktop : IDisposable
{
    public sealed class Counts
    {
        public int WheelUp, WheelDown, HWheelRight, OtherWheel;
        public int MiddleDown, MiddleUp, XDown, XUp;

        internal Counts Snapshot()
        {
            return (Counts)MemberwiseClone();
        }

        public override string ToString()
        {
            return String.Format("wheel up/down={0}/{1}, hwheel right={2}, other wheel={3}, middle down/up={4}/{5}, X down/up={6}/{7}",
                WheelUp, WheelDown, HWheelRight, OtherWheel, MiddleDown, MiddleUp, XDown, XUp);
        }
    }

    private sealed class MouseForm : Form
    {
        public Counts Events = new Counts();

        protected override void WndProc(ref Message message)
        {
            int delta = (short)(message.WParam.ToInt64() >> 16);
            switch (message.Msg)
            {
                case 0x020A: // WM_MOUSEWHEEL
                    if (delta == 200) Events.WheelUp++;
                    else if (delta == -200) Events.WheelDown++;
                    else Events.OtherWheel++;
                    break;
                case 0x020E: // WM_MOUSEHWHEEL
                    if (delta == 200) Events.HWheelRight++;
                    else Events.OtherWheel++;
                    break;
                case 0x0207: Events.MiddleDown++; break;
                case 0x0208: Events.MiddleUp++; break;
                case 0x020B: Events.XDown++; break;
                case 0x020C: Events.XUp++; break;
            }
            base.WndProc(ref message);
        }
    }

    // For mouse input, MOUSEINPUT is the largest member of the native INPUT union.
    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public MouseInput Mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int X, Y;
        public uint Data, Flags, Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X, Y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, ref Input input, int size);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern bool SetProcessDPIAware();

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    private readonly ManualResetEvent ready = new ManualResetEvent(false);
    private readonly Thread thread;
    private volatile MouseForm form;
    private Exception startupError;
    private Rectangle screen;

    public Rectangle Bounds { get; private set; }

    public const uint LeftDown = 0x0002, LeftUp = 0x0004;
    public const uint MiddleDown = 0x0020, MiddleUp = 0x0040;
    public const uint XDown = 0x0080, XUp = 0x0100;
    private const uint AbsoluteMove = 0x8001;

    public MouseTestDesktop()
    {
        SetProcessDPIAware();
        thread = new Thread(Run);
        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    private void Run()
    {
        try
        {
            screen = Screen.PrimaryScreen.Bounds;
            Rectangle work = Screen.PrimaryScreen.WorkingArea;
            int width = Math.Min(work.Width, 720);
            int height = Math.Min(work.Height, 600);
            Bounds = new Rectangle(work.X + (work.Width - width) / 2,
                work.Y + (work.Height - height) / 2, width, height);

            using (MouseForm window = new MouseForm())
            {
                window.Text = "MarbleScroll end-to-end test";
                window.FormBorderStyle = FormBorderStyle.None;
                window.StartPosition = FormStartPosition.Manual;
                window.Bounds = Bounds;
                window.TopMost = true;
                window.Shown += delegate
                {
                    form = window;
                    window.Activate();
                    ready.Set();
                };
                Application.Run(window);
            }
        }
        catch (Exception error)
        {
            startupError = error;
            ready.Set();
        }
    }

    public void WaitUntilShown()
    {
        if (!ready.WaitOne(10000))
            throw new InvalidOperationException("The test window did not appear.");
        if (startupError != null)
            throw new InvalidOperationException("The test window could not start.", startupError);
    }

    public bool IsForeground()
    {
        return (bool)form.Invoke(new Func<bool>(delegate { return GetForegroundWindow() == form.Handle; }));
    }

    public Counts ReadCounts(bool reset)
    {
        return (Counts)form.Invoke(new Func<Counts>(delegate
        {
            if (!reset)
                return form.Events.Snapshot();
            // Reset on the window thread, where all mouse messages are counted.
            Counts previous = form.Events;
            form.Events = new Counts();
            return previous;
        }));
    }

    public void Move(int x, int y)
    {
        // Target the center of the pixel's normalized range on the primary screen.
        Send(AbsoluteMove, 0, Normalize(x - screen.X, screen.Width), Normalize(y - screen.Y, screen.Height));
    }

    private static int Normalize(int offset, int size)
    {
        return (int)((offset + 0.5) * 65536 / size);
    }

    public static void Button(uint flags, uint data)
    {
        Send(flags, data, 0, 0);
    }

    private static void Send(uint flags, uint data, int x, int y)
    {
        Input input = new Input();
        input.Type = 0; // INPUT_MOUSE
        input.Mouse.Flags = flags;
        input.Mouse.Data = data;
        input.Mouse.X = x;
        input.Mouse.Y = y;
        if (SendInput(1, ref input, Marshal.SizeOf(typeof(Input))) != 1)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SendInput failed.");
    }

    public static Point Cursor()
    {
        NativePoint point;
        if (!GetCursorPos(out point))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return new Point(point.X, point.Y);
    }

    public void Dispose()
    {
        MouseForm window = form;
        if (thread.IsAlive && window != null && !window.IsDisposed)
            window.BeginInvoke(new MethodInvoker(window.Close));
        if (!thread.Join(3000))
            throw new InvalidOperationException("The test window thread did not exit.");
        ready.Dispose();
    }
}
'@

function Get-CursorDistance($From, $To) {
    return [Math]::Max([Math]::Abs($From.X - $To.X), [Math]::Abs($From.Y - $To.Y))
}

function Move-ToStart {
    $desktop.Move($startX, $startY)
    Start-Sleep -Milliseconds 80
    $point = [MouseTestDesktop]::Cursor()
    if ([Math]::Abs($point.X - $startX) -gt 3 -or [Math]::Abs($point.Y - $startY) -gt 3) {
        throw "Cursor did not reach the test position ($startX, $startY). Actual position $point."
    }
    if (-not $desktop.IsForeground()) {
        [MouseTestDesktop]::Button([MouseTestDesktop]::LeftDown, 0)
        [MouseTestDesktop]::Button([MouseTestDesktop]::LeftUp, 0)
        Start-Sleep -Milliseconds 100
        if (-not $desktop.IsForeground()) {
            throw 'The test window could not take foreground focus.'
        }
    }
}

function Invoke-Gesture([int]$Dx, [int]$Dy, [switch]$XButton) {
    $down = [MouseTestDesktop]::MiddleDown
    $up = [MouseTestDesktop]::MiddleUp
    $data = 0
    if ($XButton) {
        $down = [MouseTestDesktop]::XDown
        $up = [MouseTestDesktop]::XUp
        $data = 1 # XBUTTON1
    }

    $origin = [MouseTestDesktop]::Cursor()
    $maxMovement = 0
    $beforeRelease = $null
    [MouseTestDesktop]::Button($down, $data)
    try {
        Start-Sleep -Milliseconds 100
        if ($Dx -eq 0 -and $Dy -eq 0) {
            $beforeRelease = $desktop.ReadCounts($false)
        }
        else {
            for ($step = 1; $step -le 12; $step++) {
                $desktop.Move($startX + [int]($Dx * $step / 12), $startY + [int]($Dy * $step / 12))
                Start-Sleep -Milliseconds 25
                $movement = Get-CursorDistance $origin ([MouseTestDesktop]::Cursor())
                $maxMovement = [Math]::Max($maxMovement, $movement)
            }
        }
    }
    finally {
        [MouseTestDesktop]::Button($up, $data)
    }
    # MarbleScroll emits wheel events and replayed clicks on background tasks.
    Start-Sleep -Milliseconds 400
    $finalMovement = Get-CursorDistance $origin ([MouseTestDesktop]::Cursor())
    return @{
        Counts = $desktop.ReadCounts($false)
        BeforeRelease = $beforeRelease
        MaxMovement = [Math]::Max($maxMovement, $finalMovement)
    }
}

function Write-Result([string]$Name, [bool]$Passed, [string]$Details) {
    $status = 'FAIL'
    if ($Passed) { $status = 'PASS' }
    Write-Host "$status $Name - $Details"
}

function Test-Gesture([string]$Name, [int]$Dx, [int]$Dy, [scriptblock]$Check, [switch]$XButton) {
    try {
        Move-ToStart
        $null = $desktop.ReadCounts($true)
        $result = Invoke-Gesture $Dx $Dy -XButton:$XButton
        $passed = & $Check $result.Counts $result
        $details = "$($result.Counts), max cursor movement=$($result.MaxMovement) px"
        if ($null -ne $result.BeforeRelease) {
            $details += ", middle down/up before release=$($result.BeforeRelease.MiddleDown)/$($result.BeforeRelease.MiddleUp)"
        }
        Write-Result $Name $passed $details
        return $passed
    }
    catch {
        Write-Result $Name $false $_.Exception.Message
        return $false
    }
}

function Stop-MarbleScroll {
    if ($null -ne $app -and -not $app.HasExited) {
        Stop-Process -InputObject $app -Force
        if (-not $app.WaitForExit(5000)) {
            throw 'MarbleScroll did not exit after Stop-Process.'
        }
    }
}

$desktop = $null
$app = $null
$failed = $false
try {
    $exePath = (Resolve-Path -LiteralPath $Exe).ProviderPath
    if (Get-Process -Name MarbleScroll -ErrorAction SilentlyContinue) {
        throw 'Exit the running MarbleScroll instance before starting the test.'
    }

    $baselineName = 'Baseline without MarbleScroll'
    try {
        $desktop = New-Object MouseTestDesktop
        $desktop.WaitUntilShown()
        $bounds = $desktop.Bounds
        $startX = $bounds.Left + 30
        $startY = $bounds.Top + [int]($bounds.Height / 2)
        $vertical = [Math]::Min(280, [int]($bounds.Height / 2) - 20)
        $right = [Math]::Min(600, $bounds.Width - 60)
        $desktop.Move($startX, $startY + $vertical)
        Start-Sleep -Milliseconds 80
        $before = [MouseTestDesktop]::Cursor()
        Move-ToStart
        $movement = Get-CursorDistance $before ([MouseTestDesktop]::Cursor())
        $null = $desktop.ReadCounts($true)
        $result = Invoke-Gesture 0 0
        $baseline = $movement -ge ($vertical - 3) -and
            $result.BeforeRelease.MiddleDown -eq 1 -and
            $result.Counts.MiddleDown -eq 1 -and $result.Counts.MiddleUp -eq 1
        Write-Result $baselineName $baseline "$($result.Counts), cursor moved $movement px before clicking"
    }
    catch {
        Write-Result $baselineName $false $_.Exception.Message
        $baseline = $false
    }
    if (-not $baseline) {
        $session = [System.Diagnostics.Process]::GetCurrentProcess().SessionId
        throw "No usable interactive desktop for the baseline (Windows session $session). The test needs an unlocked desktop that accepts SendInput and delivers mouse messages to the test window."
    }

    $app = Start-Process -FilePath $exePath -PassThru
    if (-not $app.WaitForInputIdle(10000) -or $app.HasExited) {
        throw 'MarbleScroll did not start its message loop.'
    }

    $results = @(
        Test-Gesture 'Middle drag down (negative wheel, swallowed)' 0 $vertical {
            param($counts, $gesture)
            $counts.WheelDown -ge 1 -and $counts.WheelUp -eq 0 -and $counts.OtherWheel -eq 0 -and
                $counts.HWheelRight -eq 0 -and $counts.MiddleDown -eq 0 -and $counts.MiddleUp -eq 0 -and
                $gesture.MaxMovement -le 3
        }
        Test-Gesture 'Middle drag up (positive wheel)' 0 (-$vertical) {
            param($counts, $gesture)
            $counts.WheelUp -ge 1 -and $counts.WheelDown -eq 0 -and $counts.OtherWheel -eq 0 -and
                $counts.HWheelRight -eq 0 -and $counts.MiddleDown -eq 0 -and $counts.MiddleUp -eq 0 -and
                $gesture.MaxMovement -le 3
        }
        Test-Gesture 'Middle drag right (horizontal wheel)' $right 0 {
            param($counts, $gesture)
            $counts.HWheelRight -ge 1 -and $counts.WheelUp -eq 0 -and $counts.WheelDown -eq 0 -and
                $counts.OtherWheel -eq 0 -and $counts.MiddleDown -eq 0 -and $counts.MiddleUp -eq 0 -and
                $gesture.MaxMovement -le 3
        }
        Test-Gesture 'Stationary middle click (replayed after release)' 0 0 {
            param($counts, $gesture)
            $gesture.BeforeRelease.MiddleDown -eq 0 -and $gesture.BeforeRelease.MiddleUp -eq 0 -and
                $counts.MiddleDown -eq 1 -and $counts.MiddleUp -eq 1 -and $counts.WheelUp -eq 0 -and
                $counts.WheelDown -eq 0 -and $counts.HWheelRight -eq 0 -and $counts.OtherWheel -eq 0
        }
        Test-Gesture 'XBUTTON1 drag down (negative wheel, swallowed)' 0 $vertical {
            param($counts, $gesture)
            $counts.WheelDown -ge 1 -and $counts.WheelUp -eq 0 -and $counts.OtherWheel -eq 0 -and
                $counts.HWheelRight -eq 0 -and $counts.XDown -eq 0 -and $counts.XUp -eq 0 -and
                $counts.MiddleDown -eq 0 -and $counts.MiddleUp -eq 0 -and $gesture.MaxMovement -le 3
        } -XButton
    )

    Stop-MarbleScroll
    $results += Test-Gesture 'Middle drag after stopping MarbleScroll' 0 $vertical {
        param($counts, $gesture)
        $gesture.MaxMovement -ge ($vertical - 3) -and $counts.MiddleDown -eq 1 -and
            $counts.MiddleUp -eq 1 -and $counts.WheelUp -eq 0 -and $counts.WheelDown -eq 0 -and
            $counts.HWheelRight -eq 0 -and $counts.OtherWheel -eq 0
    }
    $failed = $results -contains $false
}
catch {
    Write-Result 'Test run' $false $_.Exception.Message
    $failed = $true
}
finally {
    try {
        Stop-MarbleScroll
    }
    catch {
        Write-Result 'Stop MarbleScroll during cleanup' $false $_.Exception.Message
        $failed = $true
    }
    finally {
        try {
            if ($null -ne $desktop) { $desktop.Dispose() }
        }
        catch {
            Write-Result 'Close test window' $false $_.Exception.Message
            $failed = $true
        }
        finally {
            if ($null -ne $app) { $app.Dispose() }
        }
    }
}

if ($failed) { exit 1 }
exit 0
