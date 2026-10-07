using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

internal static class GptImageSlider
{
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { internal int Left, Top, Right, Bottom; }
    // Model subtitle and its arrow stay unchanged when effort changes.
    private static readonly Rectangle referenceAnchor = new Rectangle(94, 31, 74, 13);
    private static Bitmap runtimeNeedle;
    private static Rectangle runtimeSearchArea = Rectangle.Empty;
    internal static Rectangle PopupBounds { get; private set; }
    private static Bitmap RuntimeNeedle()
    {
        if (runtimeNeedle == null)
            using (Image reference = Image.FromFile(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "GPT-effort-reference.png")))
                runtimeNeedle = Anchor(reference);
        return runtimeNeedle;
    }
    private static Rectangle PopupSearchArea(Point anchor)
    {
        // Includes all five stops and room for small shifts in popup alignment.
        return new Rectangle(anchor.X - 110, anchor.Y - 30, 320, 110);
    }
    internal static void ExpectNearButton(IntPtr hwnd, Rectangle button)
    {
        NativeRect bounds;
        if (GetWindowRect(hwnd, out bounds))
            runtimeSearchArea = new Rectangle(button.X - bounds.Left - 300,
                button.Y - bounds.Top - 220, button.Width + 600, button.Height + 380);
    }
    private static Bitmap Anchor(Image reference)
    {
        if (reference.Width != 253 || reference.Height != 137)
            throw new InvalidOperationException("The GPT reference image has unexpected dimensions.");
        Bitmap result = new Bitmap(referenceAnchor.Width, referenceAnchor.Height, PixelFormat.Format24bppRgb);
        using (Graphics graphics = Graphics.FromImage(result))
            graphics.DrawImage(reference, new Rectangle(0, 0, result.Width, result.Height), referenceAnchor, GraphicsUnit.Pixel);
        return result;
    }
    private static Point TargetPoint(Point anchor, int target)
    {
        if (target < 0 || target > 4) throw new ArgumentOutOfRangeException("target");
        // Light, Medium, High, Extra High, Ultra: x=25,76,127,178,229; y=70.
        return new Point(anchor.X - 69 + 51 * target, anchor.Y + 39);
    }
    private static bool ThumbAt(Bitmap capture, Point point, int stop)
    {
        if (point.X < 17 || point.Y < 3 || point.X + 17 >= capture.Width || point.Y + 3 >= capture.Height) return false;
        for (int y = -2; y <= 2; y++)
            for (int x = -2; x <= 2; x++)
            {
                Color color = capture.GetPixel(point.X + x, point.Y + y);
                if (color.R < 245 || color.G < 245 || color.B < 245) return false;
            }
        foreach (int offset in new[] { -16, 16 })
        {
            if ((stop == 0 && offset < 0) || (stop == 4 && offset > 0)) continue;
            Color color = capture.GetPixel(point.X + offset, point.Y);
            if (color.R >= 242 && color.G >= 242 && color.B >= 242) return false;
        }
        return true;
    }
    private static Bitmap Capture(IntPtr hwnd, bool fullWindow, out Point origin, out Point windowOrigin)
    {
        NativeRect bounds;
        if (!GetWindowRect(hwnd, out bounds)) throw new InvalidOperationException("Cannot read GPT window bounds.");
        int width = bounds.Right - bounds.Left, height = bounds.Bottom - bounds.Top;
        if (width < 253 || height < 100) throw new InvalidOperationException("GPT window is too small to locate the slider.");
        windowOrigin = new Point(bounds.Left, bounds.Top);
        Rectangle area = new Rectangle(0, 0, width, height);
        if (!fullWindow && !runtimeSearchArea.IsEmpty)
            area = Rectangle.Intersect(area, runtimeSearchArea);
        if (area.IsEmpty) area = new Rectangle(0, 0, width, height);
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
    private static bool TryCapturePopup(IntPtr hwnd, out Bitmap capture, out Point origin, out Point anchor, bool searchEntireWindow = true)
    {
        capture = null; origin = Point.Empty; anchor = Point.Empty;
        bool firstIsFull = runtimeSearchArea.IsEmpty;
        for (int attempt = 0; attempt < (firstIsFull || !searchEntireWindow ? 1 : 2); attempt++)
        {
            Point windowOrigin;
            Bitmap candidate = Capture(hwnd, firstIsFull || attempt == 1, out origin, out windowOrigin);
            try
            {
                object match = StrokesImageSearch.Match(RuntimeNeedle(), candidate);
                if (match != null)
                {
                    anchor = (Point)match;
                    PopupBounds = new Rectangle(origin.X + anchor.X - 94, origin.Y + anchor.Y - 31, 253, 100);
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
    private static int ReadThumb(Bitmap capture, Point anchor)
    {
        int found = -1;
        for (int stop = 0; stop < 5; stop++)
            if (ThumbAt(capture, TargetPoint(anchor, stop), stop))
            {
                if (found >= 0) return -1;
                found = stop;
            }
        return found;
    }
    // True means the popup anchor is visible; current=-1 means its thumb is unsettled/ambiguous.
    internal static bool TryReadCurrent(IntPtr hwnd, out int current)
    {
        current = -1;
        Point origin, anchor;
        Bitmap capture;
        if (!TryCapturePopup(hwnd, out capture, out origin, out anchor)) return false;
        using (capture)
        {
            current = ReadThumb(capture, anchor);
            return true;
        }
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
                throw new InvalidOperationException("GPT's slider thumb does not match its reported effort; no click sent.");
            Point click = TargetPoint(anchor, target);
            if (click.X < 0 || click.Y < 0 || click.X >= capture.Width || click.Y >= capture.Height) return false;
            screenPoint = new Point(origin.X + click.X, origin.Y + click.Y);
            return true;
        }
    }
    internal static bool IsPopupVisible(IntPtr hwnd)
    {
        Point origin, anchor;
        Bitmap capture;
        if (!TryCapturePopup(hwnd, out capture, out origin, out anchor)) return false;
        capture.Dispose();
        return true;
    }
    internal static bool IsPopupVisibleAtLastLocation(IntPtr hwnd)
    {
        // The input click is validated against the freshly located popup and the
        // same window size. Poll that small area while dismissal is processed.
        Point origin, anchor;
        Bitmap capture;
        if (!TryCapturePopup(hwnd, out capture, out origin, out anchor, false)) return false;
        capture.Dispose();
        return true;
    }
    internal static bool SelfTest()
    {
        // Work only with files and generated test canvases; never capture or control apps.
        using (Image reference = Image.FromFile(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "GPT-effort-reference.png")))
        using (Bitmap needle = Anchor(reference))
        using (Bitmap canvas = new Bitmap(420, 300, PixelFormat.Format24bppRgb))
        {
            using (Graphics graphics = Graphics.FromImage(canvas))
            {
                graphics.Clear(Color.Magenta);
                graphics.DrawImageUnscaled(reference, 67, 43);
            }
            object match = StrokesImageSearch.Match(needle, canvas);
            if (match == null || (Point)match != new Point(161, 74))
                throw new InvalidOperationException("GPT image selftest: anchor not found at expected offset.");
            Point anchor = (Point)match;
            int[] centers = { 92, 143, 194, 245, 296 };
            if (!ThumbAt(canvas, new Point(245, 113), 3) || ReadThumb(canvas, anchor) != 3)
                throw new InvalidOperationException("GPT image selftest: attached Extra High thumb not recognized.");
            // Real before/after screenshots: only the open popup should match the anchor.
            using (Image high = Image.FromFile(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "GPT-effort-high-reference.png")))
            using (Bitmap highCanvas = new Bitmap(high.Width, high.Height, PixelFormat.Format24bppRgb))
            {
                using (Graphics graphics = Graphics.FromImage(highCanvas)) graphics.DrawImageUnscaled(high, 0, 0);
                object highMatch = StrokesImageSearch.Match(needle, highCanvas);
                if (highMatch == null || ReadThumb(highCanvas, (Point)highMatch) != 2)
                    throw new InvalidOperationException("GPT image selftest: actual open High popup not recognized.");
            }
            using (Image closed = Image.FromFile(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "GPT-closed-reference.png")))
            using (Bitmap closedCanvas = new Bitmap(closed.Width, closed.Height, PixelFormat.Format24bppRgb))
            {
                using (Graphics graphics = Graphics.FromImage(closedCanvas)) graphics.DrawImageUnscaled(closed, 0, 0);
                if (StrokesImageSearch.Match(needle, closedCanvas) != null)
                    throw new InvalidOperationException("GPT image selftest: closed composer misidentified as a popup.");
            }
            for (int stop = 0; stop < 5; stop++)
            {
                using (Graphics graphics = Graphics.FromImage(canvas))
                {
                    graphics.FillRectangle(Brushes.White, 69, 95, 250, 37);
                    using (SolidBrush track = new SolidBrush(Color.FromArgb(227, 227, 227)))
                        graphics.FillRectangle(track, 79, 101, 230, 24);
                    graphics.FillRectangle(Brushes.White, centers[stop] - 12, 101, 24, 24);
                }
                if (TargetPoint(anchor, stop) != new Point(centers[stop], 113) ||
                    !ThumbAt(canvas, new Point(centers[stop], 113), stop) || ReadThumb(canvas, anchor) != stop)
                    throw new InvalidOperationException("GPT image selftest: stop " + stop + " failed.");
                Rectangle area = Rectangle.Intersect(PopupSearchArea(anchor), new Rectangle(Point.Empty, canvas.Size));
                using (Bitmap cropped = canvas.Clone(area, PixelFormat.Format24bppRgb))
                {
                    object croppedMatch = StrokesImageSearch.Match(needle, cropped);
                    if (croppedMatch == null || ReadThumb(cropped, (Point)croppedMatch) != stop ||
                        TargetPoint((Point)croppedMatch, stop) != new Point(centers[stop] - area.X, 113 - area.Y))
                        throw new InvalidOperationException("GPT image selftest: cropped popup coordinates failed.");
                }
                for (int other = 0; other < 5; other++)
                    if (other != stop && ThumbAt(canvas, new Point(centers[other], 113), other))
                        throw new InvalidOperationException("GPT image selftest: wrong thumb accepted.");
            }
            using (Graphics graphics = Graphics.FromImage(canvas))
            {
                using (SolidBrush track = new SolidBrush(Color.FromArgb(227, 227, 227)))
                    graphics.FillRectangle(track, 79, 101, 230, 24);
            }
            if (ReadThumb(canvas, anchor) != -1)
                throw new InvalidOperationException("GPT image selftest: absent thumb accepted.");
            using (Graphics graphics = Graphics.FromImage(canvas))
            {
                graphics.FillRectangle(Brushes.White, centers[1] - 12, 101, 24, 24);
                graphics.FillRectangle(Brushes.White, centers[3] - 12, 101, 24, 24);
            }
            if (ReadThumb(canvas, anchor) != -1)
                throw new InvalidOperationException("GPT image selftest: ambiguous thumbs accepted.");
            using (Graphics graphics = Graphics.FromImage(canvas)) graphics.Clear(Color.Magenta);
            return StrokesImageSearch.Match(needle, canvas) == null;
        }
    }
}
