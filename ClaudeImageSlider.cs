using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

// Matches pixels with StrokesPlus.net's own FindImageWithinImage function.
// Capture and input run only when the user presses a configured hotkey.
internal static class ClaudeImageSlider
{
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { internal int Left, Top, Right, Bottom; }
    private static readonly Rectangle referenceAnchor = new Rectangle(15, 61, 196, 14);
    internal static Rectangle PopupBounds { get; private set; }

    private static object Match(Bitmap needle, Bitmap haystack)
    {
        return StrokesImageSearch.Match(needle, haystack);
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
    private static Point TargetPoint(Point anchor, int target)
    {
        if (target < 0 || target > 5) throw new ArgumentOutOfRangeException("target");
        // The screenshot's High thumb is x=95. Stops are 35px apart.
        // Low, Medium, High, Extra, Max, Ultracode: x=25,60,95,130,165,200.
        return new Point(anchor.X + 10 + 35 * target, anchor.Y + 35);
    }
    internal static bool TryLocate(IntPtr hwnd, int current, int target, out Point screenPoint)
    {
        screenPoint = Point.Empty;
        NativeRect bounds;
        if (!GetWindowRect(hwnd, out bounds)) throw new InvalidOperationException("Cannot read Claude window bounds.");
        int width = bounds.Right - bounds.Left, height = bounds.Bottom - bounds.Top;
        if (width < referenceAnchor.Width || height < 100) return false;
        using (Image reference = Image.FromFile(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Claude-effort-reference.png")))
        using (Bitmap needle = Anchor(reference))
        using (Bitmap capture = new Bitmap(width, height, PixelFormat.Format24bppRgb))
        {
            using (Graphics graphics = Graphics.FromImage(capture))
                graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, capture.Size, CopyPixelOperation.SourceCopy);
            object match = Match(needle, capture);
            if (match == null) return false;
            Point anchor = (Point)match;
            PopupBounds = new Rectangle(bounds.Left + anchor.X - 15, bounds.Top + anchor.Y - 61, 224, 145);
            Point expectedThumb = TargetPoint(anchor, current);
            if (!ThumbAt(capture, expectedThumb, current))
                throw new InvalidOperationException("Claude's slider thumb does not match its reported effort; no click sent.");
            Point click = TargetPoint(anchor, target);
            if (click.X < 0 || click.Y < 0 || click.X >= capture.Width || click.Y >= capture.Height) return false;
            screenPoint = new Point(bounds.Left + click.X, bounds.Top + click.Y);
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
        // End stops have background on the outside; check only inside the track.
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
        NativeRect bounds;
        if (!GetWindowRect(hwnd, out bounds)) return false;
        int width = bounds.Right - bounds.Left, height = bounds.Bottom - bounds.Top;
        if (width < referenceAnchor.Width || height < 100) return false;
        using (Image reference = Image.FromFile(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Claude-effort-reference.png")))
        using (Bitmap needle = Anchor(reference))
        using (Bitmap capture = new Bitmap(width, height, PixelFormat.Format24bppRgb))
        {
            using (Graphics graphics = Graphics.FromImage(capture))
                graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, capture.Size, CopyPixelOperation.SourceCopy);
            return Match(needle, capture) != null;
        }
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
        NativeRect bounds;
        if (!GetWindowRect(hwnd, out bounds)) return false;
        int width = bounds.Right - bounds.Left, height = bounds.Bottom - bounds.Top;
        if (width < referenceAnchor.Width || height < 100) return false;
        using (Image reference = Image.FromFile(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Claude-effort-reference.png")))
        using (Bitmap needle = Anchor(reference))
        using (Bitmap capture = new Bitmap(width, height, PixelFormat.Format24bppRgb))
        {
            using (Graphics graphics = Graphics.FromImage(capture))
                graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, capture.Size, CopyPixelOperation.SourceCopy);
            object match = Match(needle, capture);
            if (match == null) return false;
            Point anchor = (Point)match;
            PopupBounds = new Rectangle(bounds.Left + anchor.X - 15, bounds.Top + anchor.Y - 61, 224, 145);
            current = ReadThumb(capture, anchor);
            return true;
        }
    }
    internal static bool SelfTest()
    {
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
