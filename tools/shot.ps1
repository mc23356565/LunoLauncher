# Screenshot helper: capture a window (including borderless WPF) with PrintWindow, save PNG.
# ASCII only on purpose: Windows PowerShell 5.1 reads .ps1 as ANSI when there is no BOM.
#
#   & tools\shot.ps1 -AppArgs '--page','seed' -Out dist\shots\seed-1.png
#
# Notes:
#   * the script makes itself per-monitor DPI aware, so at 150% scaling we get real pixels;
#   * launch uses ProcessStartInfo(UseShellExecute=false) because the sandbox blocks Start-Process;
#   * the app is killed after the capture unless -KeepOpen is given.

param(
    [string]$Exe = "",
    [string[]]$AppArgs = @("--page", "seed"),
    [string]$Out = "",
    [int]$WaitMs = 3000,
    [int]$TimeoutMs = 30000,
    [int]$ClickX = -1,
    [int]$ClickY = -1,
    [string]$ClickColor = "",
    [switch]$Maximize,
    [switch]$FullScreen,
    [switch]$KeepOpen
)

$src = @'
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

public static class Shot
{
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr param);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT rect, int size);

    public delegate bool EnumProc(IntPtr hwnd, IntPtr param);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    public static void MakeDpiAware()
    {
        try { SetProcessDpiAwarenessContext((IntPtr)(-4)); } catch { }
    }

    public static void Maximize(IntPtr hwnd)
    {
        ShowWindow(hwnd, 3);              // SW_MAXIMIZE
    }

    // bring the app to the front (fullscreen captures and click targeting need it)
    public static void Activate(IntPtr hwnd)
    {
        try
        {
            ShowWindow(hwnd, 3);
            SetForegroundWindow(hwnd);
            BringWindowToTop(hwnd);
            System.Threading.Thread.Sleep(300);
        }
        catch { }
    }

    // real client origin on screen, so a click can be aimed with image coordinates
    public static string Origin(IntPtr hwnd)
    {
        RECT r;
        if (DwmGetWindowAttribute(hwnd, 9, out r, Marshal.SizeOf(typeof(RECT))) != 0)
            GetWindowRect(hwnd, out r);
        return r.Left + "," + r.Top + "," + r.Right + "," + r.Bottom;
    }

    public static void Click(int x, int y)
    {
        SetCursorPos(x, y);
        System.Threading.Thread.Sleep(120);
        mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);   // LEFTDOWN
        System.Threading.Thread.Sleep(60);
        mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);   // LEFTUP
    }

    // full desktop capture: needed for popups (ComboBox dropdowns) that live in their own HWND
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);

    public static string CaptureScreen(string path, int w, int h)
    {
        IntPtr screenDc = GetDC(IntPtr.Zero);
        IntPtr memDc = CreateCompatibleDC(screenDc);
        IntPtr bmp = CreateCompatibleBitmap(screenDc, w, h);
        IntPtr old = SelectObject(memDc, bmp);
        BitBlt(memDc, 0, 0, w, h, screenDc, 0, 0, 0x00CC0020);   // SRCCOPY
        using (var img = System.Drawing.Image.FromHbitmap(bmp))
        {
            img.Save(path, ImageFormat.Png);
        }
        SelectObject(memDc, old);
        DeleteObject(bmp);
        DeleteDC(memDc);
        ReleaseDC(IntPtr.Zero, screenDc);
        return w + "x" + h;
    }

    // find the pixel of the given colour that is closest to the image centre (image = saved PNG)
    public static string FindInPng(string path, int r, int g, int b, int tol)
    {
        using (var src = new Bitmap(path))
        {
            int w = src.Width, h = src.Height;
            var data = src.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            byte[] buf = new byte[data.Stride * h];
            Marshal.Copy(data.Scan0, buf, 0, buf.Length);
            src.UnlockBits(data);
            double cx = w / 2.0, cy = h / 2.0, best = double.MaxValue;
            int bx = -1, by = -1;
            for (int y = 0; y < h; y++)
            {
                int row = y * data.Stride;
                for (int x = 0; x < w; x++)
                {
                    int o = row + x * 4;
                    // 32bppArgb memory layout is B,G,R,A
                    if (Math.Abs(buf[o] - b) <= tol && Math.Abs(buf[o + 1] - g) <= tol && Math.Abs(buf[o + 2] - r) <= tol)
                    {
                        double d = Math.Abs(x - cx) + Math.Abs(y - cy);
                        if (d < best) { best = d; bx = x; by = y; }
                    }
                }
            }
            if (bx < 0) return "";
            return (bx + data.Stride * 0) + "," + by + "," + w + "," + h;
        }
    }

    // first visible top-level window that belongs to the given pid
    public static IntPtr FindWindow(int pid)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows(delegate (IntPtr hwnd, IntPtr p)
        {
            uint wpid;
            GetWindowThreadProcessId(hwnd, out wpid);
            if (wpid == (uint)pid && IsWindowVisible(hwnd))
            {
                found = hwnd;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    public static string Capture(IntPtr hwnd, string path)
    {
        if (hwnd == IntPtr.Zero) return "no window";
        System.Threading.Thread.Sleep(400);   // let the WM_SIZE / WPF layout settle
        RECT r;
        // DWMWA_EXTENDED_FRAME_BOUNDS = 9: real bounds without the drop shadow
        if (DwmGetWindowAttribute(hwnd, 9, out r, Marshal.SizeOf(typeof(RECT))) != 0)
            GetWindowRect(hwnd, out r);
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        if (w <= 0 || h <= 0) return "empty rect " + w + "x" + h;
        using (var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb))
        {
            using (var g = Graphics.FromImage(bmp))
            {
                IntPtr hdc = g.GetHdc();
                bool ok = PrintWindow(hwnd, hdc, 2);   // PW_RENDERFULLCONTENT
                g.ReleaseHdc(hdc);
                if (!ok) return "PrintWindow failed";
            }
            bmp.Save(path, ImageFormat.Png);
        }
        return w + "x" + h;
    }
}
'@

Add-Type -TypeDefinition $src -ReferencedAssemblies System.Drawing -ErrorAction Stop
[Shot]::MakeDpiAware()

# 默认按仓库布局找 exe / 输出位置（不写死本机绝对路径）
$repo = Split-Path -Parent $PSScriptRoot
if (-not $Exe) { $Exe = Join-Path $repo "dist\LunoLauncher.exe" }
if (-not $Out) { $Out = Join-Path $repo "dist\shots\shot.png" }

Get-Process -Name LunoLauncher -ErrorAction SilentlyContinue | ForEach-Object { try { $_.Kill() } catch { } }
Start-Sleep -Milliseconds 500

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $Exe
$psi.Arguments = ($AppArgs -join ' ')
$psi.UseShellExecute = $false
$psi.WorkingDirectory = Split-Path -Parent $Exe
$proc = [System.Diagnostics.Process]::Start($psi)

$deadline = (Get-Date).AddMilliseconds($TimeoutMs)
$hwnd = [IntPtr]::Zero
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 200
    if ($proc.HasExited) { Write-Output "process exited early: $($proc.ExitCode)"; break }
    $h = [Shot]::FindWindow($proc.Id)
    if ($h -ne [IntPtr]::Zero) { $hwnd = $h; break }
}

if ($hwnd -eq [IntPtr]::Zero) {
    Write-Output "window not found"
    if (-not $proc.HasExited) { try { $proc.Kill() } catch { } }
    exit 1
}

Start-Sleep -Milliseconds $WaitMs
if ($Maximize) { [Shot]::Maximize($hwnd); Start-Sleep -Milliseconds 1200 }
if ($ClickX -ge 0 -and $ClickY -ge 0) {
    Write-Output "window rect: $([Shot]::Origin($hwnd))"
    [Shot]::Click($ClickX, $ClickY)
    Start-Sleep -Milliseconds 900
}
elseif ($ClickColor -ne "") {
    $r = [Convert]::ToInt32($ClickColor.Substring(0, 2), 16)
    $g = [Convert]::ToInt32($ClickColor.Substring(2, 2), 16)
    $b = [Convert]::ToInt32($ClickColor.Substring(4, 2), 16)
    $hit = [Shot]::FindColorCentre($hwnd, $r, $g, $b, 10)
    Write-Output "click target: $hit"
    if ($hit -match '^(\d+),(\d+)$') {
        [Shot]::Click([int]$Matches[1], [int]$Matches[2])
        Start-Sleep -Milliseconds 900
    }
}
$dir = Split-Path -Parent $Out
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }

# always take a probe capture first: clicking has to be aimed at this exact frame
$probe = Join-Path $dir "_probe.png"
$size = [Shot]::Capture($hwnd, $probe)
Write-Output "probe $probe ($size)"

if ($ClickColor -ne "") {
    $r = [Convert]::ToInt32($ClickColor.Substring(0, 2), 16)
    $g = [Convert]::ToInt32($ClickColor.Substring(2, 2), 16)
    $b = [Convert]::ToInt32($ClickColor.Substring(4, 2), 16)
    $hit = [Shot]::FindInPng($probe, $r, $g, $b, 10)
    Write-Output "found: $hit"
    if ($hit -ne "") {
        $parts = $hit.Split(',')
        [Shot]::Click([int]$parts[0], [int]$parts[1])
        Start-Sleep -Milliseconds 900
        $size = [Shot]::Capture($hwnd, $Out)
    }
}
elseif ($ClickX -ge 0 -and $ClickY -ge 0) {
    [Shot]::Click($ClickX, $ClickY)
    Start-Sleep -Milliseconds 900
    $size = [Shot]::Capture($hwnd, $Out)
}
elseif ($FullScreen) {
    # 主窗口 + 所有弹出窗口（ComboBox 下拉是独立 HWND，PrintWindow 抓不到）
    [Shot]::Activate($hwnd)
    $size = [Shot]::CaptureScreen($Out, 2560, 1600)
    Write-Output "fullscreen $size"
}
else {
    Move-Item $probe $Out -Force
}

Write-Output "saved $Out ($size) pid=$($proc.Id)"
if (-not $KeepOpen) { Start-Sleep -Milliseconds 300; try { $proc.Kill() } catch { } }
