using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

// All screenshots and clicks run only during a user-triggered hotkey.
internal static class ClaudeImageSlider
{
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { internal int Left, Top, Right, Bottom; }
    private static readonly Rectangle referenceAnchor = new Rectangle(15, 61, 196, 14);
    private static Bitmap runtimeNeedle;
    private static Rectangle runtimeSearchArea = Rectangle.Empty;
    internal static Rectangle PopupBounds { get; private set; }

    private static object Match(Bitmap needle, Bitmap haystack) { return StrokesImageSearch.Match(needle, haystack); }
    private static Bitmap RuntimeNeedle()
    {
        if (runtimeNeedle == null)
            using (Image reference = Image.FromFile(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Claude-effort-reference.png")))
                runtimeNeedle = Anchor(reference);
        return runtimeNeedle;
    }
    private static Bitmap Anchor(Image reference)
    {
        if (reference.Width != 224 || reference.Height != 170)
            throw new InvalidOperationException("The Claude reference image has unexpected dimensions.");
        Bitmap result = new Bitmap(referenceAnchor.Width, referenceAnchor.Height, PixelFormat.Format24bppRgb);
        using (Graphics graphics = Graphics.FromImage(result))
            graphics.DrawImage(reference, new Rectangle(0, 0, result.Width, result.Height), referenceAnchor, GraphicsUnit.Pixel);
        return result;
    }
    private static Rectangle PopupSearchArea(Point anchor)
    {
        return new Rectangle(anchor.X - 45, anchor.Y - 85, 320, 180);
    }
    internal static void ExpectNearButton(IntPtr hwnd, Rectangle button)
    {
        NativeRect bounds;
        if (GetWindowRect(hwnd, out bounds))
            runtimeSearchArea = new Rectangle(button.X - bounds.Left - 260,
                button.Y - bounds.Top - 240, button.Width + 520, button.Height + 440);
    }
    private static Rectangle SearchArea(Size window, bool full)
    {
        Rectangle whole = new Rectangle(Point.Empty, window);
        Rectangle area = full || runtimeSearchArea.IsEmpty ? whole : Rectangle.Intersect(whole, runtimeSearchArea);
        return area.IsEmpty ? whole : area;
    }
    private static Bitmap Capture(IntPtr hwnd, bool full, out Point origin, out Point windowOrigin)
    {
        NativeRect bounds;
        if (!GetWindowRect(hwnd, out bounds)) throw new InvalidOperationException("Cannot read Claude window bounds.");
        int width = bounds.Right - bounds.Left, height = bounds.Bottom - bounds.Top;
        if (width < referenceAnchor.Width || height < 100)
            throw new InvalidOperationException("Claude window is too small to locate the slider.");
        windowOrigin = new Point(bounds.Left, bounds.Top);
        Rectangle area = SearchArea(new Size(width, height), full);
        origin = new Point(bounds.Left + area.X, bounds.Top + area.Y);
        System.Diagnostics.Stopwatch captureTimer = System.Diagnostics.Stopwatch.StartNew();
        Bitmap capture = new Bitmap(area.Width, area.Height, PixelFormat.Format24bppRgb);
        try
        {
            using (Graphics graphics = Graphics.FromImage(capture))
                graphics.CopyFromScreen(origin.X, origin.Y, 0, 0, capture.Size, CopyPixelOperation.SourceCopy);
            StrokesImageSearch.RecordCapture(area.Width == width && area.Height == height, captureTimer.ElapsedMilliseconds);
            return capture;
        }
        catch { capture.Dispose(); throw; }
    }
    private static bool TryCapturePopup(IntPtr hwnd, out Bitmap capture, out Point origin, out Point anchor)
    {
        capture = null; origin = Point.Empty; anchor = Point.Empty;
        bool firstIsFull = runtimeSearchArea.IsEmpty;
        for (int attempt = 0; attempt < (firstIsFull ? 1 : 2); attempt++)
        {
            Point windowOrigin;
            Bitmap candidate = Capture(hwnd, firstIsFull || attempt == 1, out origin, out windowOrigin);
            try
            {
                object match = Match(RuntimeNeedle(), candidate);
                if (match != null)
                {
                    anchor = (Point)match;
                    PopupBounds = new Rectangle(origin.X + anchor.X - 15, origin.Y + anchor.Y - 61, 224, 145);
                    runtimeSearchArea = PopupSearchArea(new Point(origin.X + anchor.X - windowOrigin.X,
                        origin.Y + anchor.Y - windowOrigin.Y));
                    capture = candidate;
                    return true;
                }
            }
            catch { candidate.Dispose(); throw; }
            candidate.Dispose();
        }
        return false;
    }
    private static Point TargetPoint(Point anchor, int target)
    {
        if (target < 0 || target > 5) throw new ArgumentOutOfRangeException("target");
        // Low, Medium, High, Extra, Max, Ultracode: x=25,60,95,130,165,200.
        return new Point(anchor.X + 10 + 35 * target, anchor.Y + 35);
    }
    internal static bool TryLocate(IntPtr hwnd, int current, int target, out Point screenPoint)
    {
        screenPoint = Point.Empty;
        Point origin, anchor;
        Bitmap capture;
        if (!TryCapturePopup(hwnd, out capture, out origin, out anchor)) return false;
        using (capture)
        {
            if (!ThumbAt(capture, TargetPoint(anchor, current), current))
                throw new InvalidOperationException("Claude's slider thumb does not match its reported effort; no click sent.");
            Point click = TargetPoint(anchor, target);
            if (click.X < 0 || click.Y < 0 || click.X >= capture.Width || click.Y >= capture.Height) return false;
            screenPoint = new Point(origin.X + click.X, origin.Y + click.Y);
            return true;
        }
    }
    private static bool ThumbAt(Bitmap capture, Point point, int stop)
    {
        if (point.X < 3 || point.Y < 3 || point.X + 3 >= capture.Width || point.Y + 3 >= capture.Height) return false;
        for (int y = -2; y <= 2; y++)
            for (int x = -2; x <= 2; x++)
            {
                Color color = capture.GetPixel(point.X + x, point.Y + y);
                if (color.R < 245 || color.G < 245 || color.B < 245) return false;
            }
        foreach (int offset in new[] { -12, 12 })
        {
            if ((stop == 0 && offset < 0) || (stop == 5 && offset > 0)) continue;
            Color color = capture.GetPixel(point.X + offset, point.Y);
            if (color.R >= 242 && color.G >= 242 && color.B >= 242) return false;
        }
        return true;
    }
    internal static bool IsPopupVisible(IntPtr hwnd)
    {
        Point origin, anchor;
        Bitmap capture;
        if (!TryCapturePopup(hwnd, out capture, out origin, out anchor)) return false;
        capture.Dispose();
        return true;
    }
    private static int ReadThumb(Bitmap capture, Point anchor)
    {
        int found = -1;
        for (int stop = 0; stop < 6; stop++)
            if (ThumbAt(capture, TargetPoint(anchor, stop), stop))
            {
                if (found >= 0) return -1;
                found = stop;
            }
        return found;
    }
    internal static bool TryReadCurrent(IntPtr hwnd, out int current)
    {
        current = -1;
        Point origin, anchor;
        Bitmap capture;
        if (!TryCapturePopup(hwnd, out capture, out origin, out anchor)) return false;
        using (capture) current = ReadThumb(capture, anchor);
        return true;
    }
    private static void SearchAreaSelfTest()
    {
        Rectangle previous = runtimeSearchArea;
        try
        {
            Point anchor = new Point(88, 108);
            runtimeSearchArea = PopupSearchArea(anchor);
            Rectangle area = SearchArea(new Size(400, 300), false);
            for (int stop = 0; stop < 6; stop++)
                if (!area.Contains(TargetPoint(anchor, stop)))
                    throw new InvalidOperationException("Claude cropped search clipped a slider stop.");
            if (!area.Contains(new Rectangle(anchor, referenceAnchor.Size)) || area.Width > 320 || area.Height > 180)
                throw new InvalidOperationException("Claude cropped search did not contain the anchor.");
            runtimeSearchArea = new Rectangle(-30, -20, 320, 180);
            if (SearchArea(new Size(400, 300), false) != new Rectangle(0, 0, 290, 160))
                throw new InvalidOperationException("Claude edge crop was not clipped to the window.");
            runtimeSearchArea = new Rectangle(800, 600, 320, 180);
            if (SearchArea(new Size(400, 300), false) != new Rectangle(0, 0, 400, 300))
                throw new InvalidOperationException("Claude stale search area did not recover the full window.");
        }
        finally { runtimeSearchArea = previous; }
    }
    internal static bool SelfTest()
    {
        SearchAreaSelfTest();
        // No screen capture, app access, mouse or keyboard input in this test.
        using (Image reference = Image.FromFile(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Claude-effort-reference.png")))
        using (Bitmap needle = Anchor(reference))
        using (Bitmap canvas = new Bitmap(400, 300, PixelFormat.Format24bppRgb))
        {
            using (Graphics graphics = Graphics.FromImage(canvas))
            {
                graphics.Clear(Color.Magenta);
                graphics.DrawImageUnscaled(reference, 73, 47);
            }
            object match = Match(needle, canvas);
            if (match == null || (Point)match != new Point(88, 108))
                throw new InvalidOperationException("Image selftest: expected anchor (88,108), found " + (match == null ? "none" : match.ToString()));
            Point anchor = (Point)match;
            if (TargetPoint(anchor, 0) != new Point(98, 143) ||
                TargetPoint(anchor, 2) != new Point(168, 143) ||
                TargetPoint(anchor, 5) != new Point(273, 143) ||
                !ThumbAt(canvas, TargetPoint(anchor, 2), 2) || ThumbAt(canvas, TargetPoint(anchor, 1), 1))
                throw new InvalidOperationException("Image selftest: slider point/thumb validation failed; center=" + canvas.GetPixel(168, 143) + "; left=" + canvas.GetPixel(156, 143) + "; right=" + canvas.GetPixel(180, 143));
            // Exercise all six thumb positions, including white background outside the end stops.
            int[] centers = { 98, 133, 168, 203, 238, 273 };
            for (int stop = 0; stop < 6; stop++)
            {
                using (Graphics graphics = Graphics.FromImage(canvas))
                {
                    graphics.FillRectangle(Brushes.White, 75, 127, 220, 32);
                    using (SolidBrush track = new SolidBrush(Color.FromArgb(227, 227, 227)))
                        graphics.FillRectangle(track, 88, 133, 196, 20);
                    graphics.FillRectangle(Brushes.White, centers[stop] - 8, 133, 16, 20);
                }
                if (TargetPoint(anchor, stop) != new Point(centers[stop], 143) ||
                    !ThumbAt(canvas, new Point(centers[stop], 143), stop) || ReadThumb(canvas, anchor) != stop)
                    throw new InvalidOperationException("Image selftest: stop " + stop + " failed.");
                // The runtime crop must preserve the same stop and screen point.
                Rectangle area = Rectangle.Intersect(PopupSearchArea(anchor), new Rectangle(Point.Empty, canvas.Size));
                using (Bitmap cropped = canvas.Clone(area, PixelFormat.Format24bppRgb))
                {
                    object croppedMatch = Match(needle, cropped);
                    if (croppedMatch == null || ReadThumb(cropped, (Point)croppedMatch) != stop)
                        throw new InvalidOperationException("Claude cropped image lost stop " + stop + ".");
                    Point localPoint = TargetPoint((Point)croppedMatch, stop);
                    Point screenOrigin = new Point(-1920, 100);
                    Point actual = new Point(screenOrigin.X + area.X + localPoint.X, screenOrigin.Y + area.Y + localPoint.Y);
                    if (actual != new Point(screenOrigin.X + centers[stop], screenOrigin.Y + 143))
                        throw new InvalidOperationException("Claude crop changed the screen coordinate for stop " + stop + ".");
                }
                for (int other = 0; other < 6; other++)
                    if (other != stop && ThumbAt(canvas, new Point(centers[other], 143), other))
                        throw new InvalidOperationException("Image selftest: wrong thumb accepted.");
            }
            using (Graphics graphics = Graphics.FromImage(canvas))
            {
                using (SolidBrush track = new SolidBrush(Color.FromArgb(227, 227, 227)))
                    graphics.FillRectangle(track, 88, 133, 196, 20);
            }
            if (ReadThumb(canvas, anchor) != -1)
                throw new InvalidOperationException("Claude image selftest: missing thumb accepted.");
            using (Graphics graphics = Graphics.FromImage(canvas))
            {
                graphics.FillRectangle(Brushes.White, centers[1] - 8, 133, 16, 20);
                graphics.FillRectangle(Brushes.White, centers[3] - 8, 133, 16, 20);
            }
            if (ReadThumb(canvas, anchor) != -1)
                throw new InvalidOperationException("Claude image selftest: ambiguous thumbs accepted.");
            using (Graphics graphics = Graphics.FromImage(canvas)) graphics.Clear(Color.Magenta);
            return Match(needle, canvas) == null;
        }
    }
}
