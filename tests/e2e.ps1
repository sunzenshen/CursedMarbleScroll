param(
    [Parameter(Mandatory = $true)]
    [string]$Exe,
    [ValidateSet('32', '64')]
    [int]$ExpectedBits = ([IntPtr]::Size * 8)
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
        public int MiddleDown, MiddleUp, XDown, XUp, LeftDown, LeftUp, RightDown, RightUp;
        public int VerticalTotal, HorizontalTotal, PartialWheel, SubNotchWheel;
        public int LastXDown, LastXUp;
        public Point LastMiddleDown;

        internal Counts Snapshot()
        {
            return (Counts)MemberwiseClone();
        }

        public override string ToString()
        {
            return String.Format("wheel up/down={0}/{1}, hwheel right={2}, other wheel={3}, middle down/up={4}/{5}, X down/up={6}/{7}, wheel totals V/H={8}/{9}, partial/sub-notch={10}/{11}",
                WheelUp, WheelDown, HWheelRight, OtherWheel, MiddleDown, MiddleUp, XDown, XUp, VerticalTotal, HorizontalTotal, PartialWheel, SubNotchWheel);
        }
    }

    private sealed class MouseForm : Form
    {
        public Counts MouseCounts = new Counts();

        protected override void WndProc(ref Message message)
        {
            int delta = (short)(message.WParam.ToInt64() >> 16);
            switch (message.Msg)
            {
                case 0x020A: // WM_MOUSEWHEEL
                    MouseCounts.VerticalTotal += delta;
                    if (delta != 0 && Math.Abs(delta) % 120 != 0) MouseCounts.PartialWheel++;
                    if (delta != 0 && Math.Abs(delta) < 120) MouseCounts.SubNotchWheel++;
                    if (delta > 0) MouseCounts.WheelUp++;
                    else if (delta < 0) MouseCounts.WheelDown++;
                    else MouseCounts.OtherWheel++;
                    break;
                case 0x020E: // WM_MOUSEHWHEEL
                    MouseCounts.HorizontalTotal += delta;
                    if (delta != 0 && Math.Abs(delta) % 120 != 0) MouseCounts.PartialWheel++;
                    if (delta != 0 && Math.Abs(delta) < 120) MouseCounts.SubNotchWheel++;
                    if (delta > 0) MouseCounts.HWheelRight++;
                    else MouseCounts.OtherWheel++;
                    break;
                case 0x0201: MouseCounts.LeftDown++; break;
                case 0x0202: MouseCounts.LeftUp++; break;
                case 0x0204: MouseCounts.RightDown++; break;
                case 0x0205: MouseCounts.RightUp++; break;
                case 0x0207:
                case 0x0209:
                    MouseCounts.MiddleDown++;
                    long position = message.LParam.ToInt64();
                    MouseCounts.LastMiddleDown = PointToScreen(new Point((short)position, (short)(position >> 16)));
                    break;
                case 0x0208: MouseCounts.MiddleUp++; break;
                case 0x020B:
                case 0x020D:
                    MouseCounts.XDown++;
                    MouseCounts.LastXDown = delta;
                    break;
                case 0x020C:
                    MouseCounts.XUp++;
                    MouseCounts.LastXUp = delta;
                    break;
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

    public const uint LeftDown = 0x0002, LeftUp = 0x0004, RightDown = 0x0008, RightUp = 0x0010;
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
                return form.MouseCounts.Snapshot();
            // Reset on the window thread, where all mouse messages are counted.
            Counts previous = form.MouseCounts;
            form.MouseCounts = new Counts();
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
        input.Mouse.Time = 0; // Let Windows supply the timestamp.
        input.Mouse.ExtraInfo = UIntPtr.Zero;
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

function Invoke-Gesture([int]$Dx, [int]$Dy, [int]$SideButton = 0, [int]$HoldMs = 100) {
    $down = [MouseTestDesktop]::MiddleDown
    $up = [MouseTestDesktop]::MiddleUp
    $data = 0
    if ($SideButton) {
        $down = [MouseTestDesktop]::XDown
        $up = [MouseTestDesktop]::XUp
        $data = $SideButton
    }

    $origin = [MouseTestDesktop]::Cursor()
    $maxMovement = 0
    $beforeRelease = $null
    [MouseTestDesktop]::Button($down, $data)
    try {
        Start-Sleep -Milliseconds $HoldMs
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
    # Allow the output worker and test window to drain their queues.
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

function Test-Gesture([string]$Name, [int]$Dx, [int]$Dy, [scriptblock]$Check, [int]$SideButton = 0, [int]$HoldMs = 100) {
    try {
        Move-ToStart
        $null = $desktop.ReadCounts($true)
        $result = Invoke-Gesture $Dx $Dy -SideButton $SideButton -HoldMs $HoldMs
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

function Start-MarbleScroll([string]$Options) {
    $script:app = Start-Process -FilePath $exePath -ArgumentList $Options -PassThru
    if (-not $app.WaitForInputIdle(10000) -or $app.HasExited) {
        throw 'MarbleScroll did not start its message loop.'
    }
}

function Wait-MarbleScrollHook {
    $timer = [System.Diagnostics.Stopwatch]::StartNew()
    while ($timer.ElapsedMilliseconds -lt 3000 -and -not $app.HasExited) {
        Move-ToStart
        $null = $desktop.ReadCounts($true)
        [MouseTestDesktop]::Button([MouseTestDesktop]::MiddleDown, 0)
        try {
            $desktop.Move($startX, $startY + 24)
        }
        finally {
            [MouseTestDesktop]::Button([MouseTestDesktop]::MiddleUp, 0)
        }

        $attempt = [System.Diagnostics.Stopwatch]::StartNew()
        do {
            $counts = $desktop.ReadCounts($false)
            if ($counts.WheelDown -gt 0) {
                return
            }
            Start-Sleep -Milliseconds 20
        } while ($attempt.ElapsedMilliseconds -lt 150 -and $timer.ElapsedMilliseconds -lt 3000)
    }
    throw 'MarbleScroll did not produce a wheel event in the startup readiness probe.'
}

function Send-Click([int]$SideButton = 0) {
    if ($SideButton) {
        [MouseTestDesktop]::Button([MouseTestDesktop]::XDown, $SideButton)
        [MouseTestDesktop]::Button([MouseTestDesktop]::XUp, $SideButton)
    }
    else {
        [MouseTestDesktop]::Button([MouseTestDesktop]::MiddleDown, 0)
        [MouseTestDesktop]::Button([MouseTestDesktop]::MiddleUp, 0)
    }
}

function Stop-MarbleScroll {
    if ($null -ne $app -and -not $app.HasExited) {
        Stop-Process -InputObject $app -Force
        if (-not $app.WaitForExit(5000)) {
            throw 'MarbleScroll did not exit after Stop-Process.'
        }
    }
    if ($null -ne $app) {
        $app.Dispose()
        $script:app = $null
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

    $layout = Start-Process -FilePath $exePath -ArgumentList "--check-native-layouts $ExpectedBits" -PassThru
    try {
        if (-not $layout.WaitForExit(2000)) {
            Stop-Process -InputObject $layout -Force
            throw 'Native layout check did not exit.'
        }
        if ($layout.ExitCode -ne 0) {
            throw "Native INPUT or MSLLHOOKSTRUCT layout did not match the $ExpectedBits-bit expectation."
        }
        Write-Result 'Native input layouts' $true "$ExpectedBits-bit app"
    }
    finally {
        $layout.Dispose()
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

    Start-MarbleScroll '--defaults'
    Wait-MarbleScrollHook

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
        foreach ($side in 1, 2) {
            Test-Gesture "XBUTTON$side drag down (negative wheel, swallowed)" 0 $vertical {
                param($counts, $gesture)
                $counts.WheelDown -ge 1 -and $counts.WheelUp -eq 0 -and $counts.OtherWheel -eq 0 -and
                    $counts.HWheelRight -eq 0 -and $counts.XDown -eq 0 -and $counts.XUp -eq 0 -and
                    $counts.MiddleDown -eq 0 -and $counts.MiddleUp -eq 0 -and $gesture.MaxMovement -le 3
            } -SideButton $side
        }
    )

    $results += Test-Gesture 'Small moves produce smooth deltas at about four units per pixel' 0 24 {
        param($counts, $gesture)
        # Each absolute move is measured from the frozen point. The displacements are 2, 4, ..., 24.
        $expected = -(2 * (12 * 13 / 2) * 4)
        $counts.SubNotchWheel -gt 0 -and $counts.WheelUp -eq 0 -and $counts.HorizontalTotal -eq 0 -and
            [Math]::Abs($counts.VerticalTotal - $expected) -le 48 -and $gesture.MaxMovement -le 3
    }

    $results += Test-Gesture 'A long stationary middle press still replays one click' 0 0 {
        param($counts, $gesture)
        $gesture.BeforeRelease.MiddleDown -eq 0 -and $gesture.BeforeRelease.MiddleUp -eq 0 -and
            $counts.MiddleDown -eq 1 -and $counts.MiddleUp -eq 1 -and $counts.VerticalTotal -eq 0
    } -HoldMs 1000

    $results += Test-Gesture 'Default diagonal drag scrolls on both axes' 24 $vertical {
        param($counts, $gesture)
        $counts.WheelDown -ge 1 -and $counts.HorizontalTotal -gt 0 -and
            $counts.HWheelRight -gt 0 -and $counts.OtherWheel -eq 0 -and $gesture.MaxMovement -le 3
    }

    $second = Start-Process -FilePath $exePath -ArgumentList '--defaults' -PassThru
    try {
        $secondExited = $second.WaitForExit(2000)
        $singleInstance = $secondExited -and $second.ExitCode -eq 0 -and -not $app.HasExited
        Write-Result 'Second instance exits while the first stays running' $singleInstance "exited=$secondExited"
        $results += $singleInstance
    }
    finally {
        if (-not $second.HasExited) { Stop-Process -InputObject $second -Force }
        $second.Dispose()
    }

    Stop-MarbleScroll
    $results += Test-Gesture 'Middle drag after stopping MarbleScroll' 0 $vertical {
        param($counts, $gesture)
        $gesture.MaxMovement -ge ($vertical - 3) -and $counts.MiddleDown -eq 1 -and
            $counts.MiddleUp -eq 1 -and $counts.WheelUp -eq 0 -and $counts.WheelDown -eq 0 -and
            $counts.HWheelRight -eq 0 -and $counts.OtherWheel -eq 0
    }

    Start-MarbleScroll '--defaults --snap-to-axis'
    $results += Test-Gesture 'Mostly vertical diagonal drag snaps to vertical' 24 $vertical {
        param($counts, $gesture)
        $counts.WheelDown -ge 1 -and $counts.HorizontalTotal -eq 0 -and
            $counts.HWheelRight -eq 0 -and $counts.OtherWheel -eq 0 -and $gesture.MaxMovement -le 3
    }
    Stop-MarbleScroll
    Start-MarbleScroll '--defaults --notches'
    $results += Test-Gesture 'Notch mode emits only whole notches' 0 24 {
        param($counts, $gesture)
        $counts.PartialWheel -eq 0 -and $counts.WheelUp -eq 0 -and
            $counts.VerticalTotal -le -480 -and $counts.VerticalTotal -ge -600 -and $gesture.MaxMovement -le 3
    }

    # Flick: a fast steady drag released while still moving. Moves are measured from the frozen point.
    function Invoke-Flick {
        Move-ToStart
        $null = $desktop.ReadCounts($true)
        [MouseTestDesktop]::Button([MouseTestDesktop]::MiddleDown, 0)
        try {
            for ($step = 1; $step -le 10; $step++) {
                $desktop.Move($startX, $startY + 8)
                Start-Sleep -Milliseconds 10
            }
        }
        finally {
            [MouseTestDesktop]::Button([MouseTestDesktop]::MiddleUp, 0)
        }
        Start-Sleep -Milliseconds 30
        return $desktop.ReadCounts($false)
    }

    Stop-MarbleScroll
    Start-MarbleScroll '--defaults'
    $released = Invoke-Flick
    Start-Sleep -Milliseconds 800
    $after = $desktop.ReadCounts($false)
    $noGlide = $released.VerticalTotal -lt 0 -and $after.VerticalTotal -eq $released.VerticalTotal
    Write-Result 'A flick stops on release when momentum is off' $noGlide "at release V=$($released.VerticalTotal), later V=$($after.VerticalTotal)"
    $results += $noGlide

    Stop-MarbleScroll
    Start-MarbleScroll '--defaults --momentum'
    $released = Invoke-Flick
    Start-Sleep -Milliseconds 2000
    $glided = $desktop.ReadCounts($false)
    Start-Sleep -Milliseconds 800
    $settled = $desktop.ReadCounts($false)
    $glide = $released.VerticalTotal - $glided.VerticalTotal
    $momentumGlide = $glide -ge 120 -and $glided.WheelUp -eq 0 -and $settled.VerticalTotal -eq $glided.VerticalTotal -and
        $glided.MiddleDown -eq 0 -and $glided.MiddleUp -eq 0
    Write-Result 'A flick glides after release and then stops' $momentumGlide "gesture V=$($released.VerticalTotal), glide $glide units, $glided"
    $results += $momentumGlide

    $released = Invoke-Flick
    Start-Sleep -Milliseconds 70
    [MouseTestDesktop]::Button([MouseTestDesktop]::LeftDown, 0)
    [MouseTestDesktop]::Button([MouseTestDesktop]::LeftUp, 0)
    Start-Sleep -Milliseconds 60
    $clicked = $desktop.ReadCounts($false)
    Start-Sleep -Milliseconds 800
    $later = $desktop.ReadCounts($false)
    $stopGlide = $later.VerticalTotal -eq $clicked.VerticalTotal -and
        ($released.VerticalTotal - $later.VerticalTotal) -lt $glide -and $later.LeftDown -eq 1
    Write-Result 'A click stops the glide' $stopGlide "glide before click $($released.VerticalTotal - $clicked.VerticalTotal) units, after click $($clicked.VerticalTotal - $later.VerticalTotal) units"
    $results += $stopGlide

    Stop-MarbleScroll
    Start-MarbleScroll '--defaults'
    $interval = 300
    foreach ($button in 0, 1, 2) {
        Move-ToStart
        $null = $desktop.ReadCounts($true)
        $timer = [System.Diagnostics.Stopwatch]::StartNew()
        Send-Click $button
        Start-Sleep -Milliseconds 100
        $early = $desktop.ReadCounts($false)
        $earlyMs = $timer.ElapsedMilliseconds
        do {
            Start-Sleep -Milliseconds 15
            $late = $desktop.ReadCounts($false)
        } while ($late.MiddleUp -eq 0 -and $timer.ElapsedMilliseconds -lt ($interval + 250))
        $replayMs = $timer.ElapsedMilliseconds
        $single = ($earlyMs -ge $interval -or ($early.MiddleDown -eq 0 -and $early.MiddleUp -eq 0)) -and
            $replayMs -ge ($interval - 25) -and $late.MiddleDown -eq 1 -and $late.MiddleUp -eq 1 -and
            $late.XDown -eq 0 -and $late.XUp -eq 0 -and $late.VerticalTotal -eq 0 -and $late.HorizontalTotal -eq 0
        $name = @('Middle', 'XBUTTON1', 'XBUTTON2')[$button]
        Write-Result "$name single click replays as middle after the default lock window" $single "at ${earlyMs}ms $early; at ${replayMs}ms $late"
        $results += $single
    }

    Move-ToStart
    $origin = [MouseTestDesktop]::Cursor()
    $null = $desktop.ReadCounts($true)
    Send-Click
    Start-Sleep -Milliseconds 50
    # Moving within the lock window ends the wait. The replay must land where the button was pressed.
    $desktop.Move($origin.X, $origin.Y + 30)
    Start-Sleep -Milliseconds 150
    $moved = $desktop.ReadCounts($false)
    $desktop.Move($origin.X, $origin.Y + 40)
    Start-Sleep -Milliseconds 80
    $resumed = Get-CursorDistance $origin ([MouseTestDesktop]::Cursor())
    $offset = Get-CursorDistance $origin $moved.LastMiddleDown
    $inPlace = $moved.MiddleDown -eq 1 -and $moved.MiddleUp -eq 1 -and $offset -le 3 -and $resumed -ge 37
    Write-Result 'Click followed by motion replays at the press position' $inPlace "$moved, click offset $offset px, cursor then moved $resumed px"
    $results += $inPlace

    $null = $desktop.ReadCounts($true)
    $origin = [MouseTestDesktop]::Cursor()
    Send-Click
    Send-Click
    $desktop.Move($origin.X, $origin.Y + 20)
    Start-Sleep -Milliseconds 120
    $lockedCounts = $desktop.ReadCounts($false)
    $lockedMovement = Get-CursorDistance $origin ([MouseTestDesktop]::Cursor())
    $locked = $lockedCounts.VerticalTotal -lt 0 -and $lockedCounts.MiddleDown -eq 0 -and
        $lockedCounts.MiddleUp -eq 0 -and $lockedMovement -le 3
    Write-Result 'Double-click locks scrolling without holding a button' $locked "$lockedCounts, cursor moved $lockedMovement px"
    $results += $locked

    Send-Click
    $desktop.Move($origin.X, $origin.Y + 40)
    Start-Sleep -Milliseconds ($interval + 200)
    $unlockedCounts = $desktop.ReadCounts($false)
    $unlockedMovement = Get-CursorDistance $origin ([MouseTestDesktop]::Cursor())
    $unlocked = $unlockedMovement -ge 37 -and $unlockedCounts.VerticalTotal -eq $lockedCounts.VerticalTotal -and
        $unlockedCounts.MiddleDown -eq 0 -and $unlockedCounts.MiddleUp -eq 0
    Write-Result 'One click unlocks and restores pointer movement' $unlocked "$unlockedCounts, cursor moved $unlockedMovement px"
    $results += $unlocked

    foreach ($button in 'Left', 'Right') {
        Move-ToStart
        $origin = [MouseTestDesktop]::Cursor()
        $null = $desktop.ReadCounts($true)
        Send-Click
        Send-Click
        $desktop.Move($origin.X, $origin.Y + 20)
        Start-Sleep -Milliseconds 120
        $whileLocked = $desktop.ReadCounts($true)
        [MouseTestDesktop]::Button([MouseTestDesktop]::($button + 'Down'), 0)
        [MouseTestDesktop]::Button([MouseTestDesktop]::($button + 'Up'), 0)
        $desktop.Move($origin.X, $origin.Y + 40)
        Start-Sleep -Milliseconds 120
        $afterUnlock = $desktop.ReadCounts($false)
        $moved = Get-CursorDistance $origin ([MouseTestDesktop]::Cursor())
        # The next click after unlocking reaches the app normally.
        [MouseTestDesktop]::Button([MouseTestDesktop]::($button + 'Down'), 0)
        [MouseTestDesktop]::Button([MouseTestDesktop]::($button + 'Up'), 0)
        Start-Sleep -Milliseconds 80
        $next = $desktop.ReadCounts($false)
        $cancels = $whileLocked.VerticalTotal -lt 0 -and
            $afterUnlock.($button + 'Down') -eq 0 -and $afterUnlock.($button + 'Up') -eq 0 -and
            $afterUnlock.VerticalTotal -eq 0 -and $moved -ge 37 -and
            $next.($button + 'Down') -eq 1 -and $next.($button + 'Up') -eq 1
        Write-Result "$button click cancels scroll lock without clicking" $cancels (
            "locked V=$($whileLocked.VerticalTotal); after unlock $afterUnlock, cursor moved $moved px; next click down/up=$($next.($button + 'Down'))/$($next.($button + 'Up'))")
        $results += $cancels
    }

    Move-ToStart
    $origin = [MouseTestDesktop]::Cursor()
    $null = $desktop.ReadCounts($true)
    Send-Click 2
    Send-Click 2
    $desktop.Move($origin.X, $origin.Y + 20)
    Start-Sleep -Milliseconds ($interval + 100)
    $counts = $desktop.ReadCounts($false)
    $movement = Get-CursorDistance $origin ([MouseTestDesktop]::Cursor())
    $sideLock = $counts.VerticalTotal -lt 0 -and $movement -le 3 -and
        $counts.XDown -eq 0 -and $counts.XUp -eq 0 -and $counts.MiddleDown -eq 0 -and $counts.MiddleUp -eq 0
    Write-Result 'XBUTTON2 double-click locks scrolling' $sideLock "$counts, cursor moved $movement px"
    $results += $sideLock

    Stop-MarbleScroll
    Start-MarbleScroll '--defaults --lock-idle-ms 1500'
    Move-ToStart
    $origin = [MouseTestDesktop]::Cursor()
    $null = $desktop.ReadCounts($true)
    Send-Click
    Send-Click
    $desktop.Move($origin.X, $origin.Y + 20)
    Start-Sleep -Milliseconds 150
    $beforeIdle = $desktop.ReadCounts($false)
    Start-Sleep -Milliseconds 1500
    $desktop.Move($origin.X, $origin.Y + 40)
    Start-Sleep -Milliseconds 150
    $afterIdle = $desktop.ReadCounts($false)
    $idleMovement = Get-CursorDistance $origin ([MouseTestDesktop]::Cursor())
    $idle = $beforeIdle.VerticalTotal -lt 0 -and $beforeIdle.MiddleDown -eq 0 -and
        $afterIdle.VerticalTotal -eq $beforeIdle.VerticalTotal -and $idleMovement -ge 37
    Write-Result 'Locked scroll expires after the configured idle timeout' $idle "$afterIdle, cursor moved $idleMovement px"
    $results += $idle

    Stop-MarbleScroll
    Start-MarbleScroll '--defaults --no-side-buttons'
    foreach ($side in 1, 2) {
        $results += Test-Gesture "XBUTTON$side click and pointer motion pass through with side buttons disabled" 0 $vertical {
            param($counts, $gesture)
            $gesture.MaxMovement -ge ($vertical - 3) -and
                $counts.XDown -eq 1 -and $counts.XUp -eq 1 -and
                $counts.LastXDown -eq $side -and $counts.LastXUp -eq $side -and
                $counts.MiddleDown -eq 0 -and $counts.MiddleUp -eq 0 -and
                $counts.VerticalTotal -eq 0 -and $counts.HorizontalTotal -eq 0
        } -SideButton $side
    }

    Stop-MarbleScroll
    Start-MarbleScroll '--defaults --paused'
    $normalInput = {
        param($counts, $gesture)
        $gesture.MaxMovement -ge ($vertical - 3) -and $counts.MiddleDown -eq 1 -and
            $counts.MiddleUp -eq 1 -and $counts.VerticalTotal -eq 0 -and $counts.HorizontalTotal -eq 0
    }
    $results += Test-Gesture 'Pause passes a middle drag through' 0 $vertical $normalInput
    $results += Test-Gesture 'Pause passes a back-button drag through' 0 $vertical {
        param($counts, $gesture)
        $gesture.MaxMovement -ge ($vertical - 3) -and $counts.XDown -eq 1 -and
            $counts.XUp -eq 1 -and $counts.VerticalTotal -eq 0 -and $counts.HorizontalTotal -eq 0
    } -SideButton 1

    Stop-MarbleScroll
    Start-MarbleScroll '--defaults --exit-after-ms 1200'
    Move-ToStart
    [MouseTestDesktop]::Button([MouseTestDesktop]::MiddleDown, 0)
    try {
        $desktop.Move($startX, $startY + 20)
        $exited = $app.WaitForExit(2000)
    }
    finally {
        [MouseTestDesktop]::Button([MouseTestDesktop]::MiddleUp, 0)
    }
    $graceful = $exited -and $app.ExitCode -eq 0
    Write-Result 'Graceful tray exit within 2 seconds' $graceful "exited=$exited"
    $results += $graceful
    if ($graceful) {
        $results += Test-Gesture 'Input is normal after graceful exit' 0 $vertical $normalInput
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
    }
}

if ($failed) { exit 1 }
exit 0
