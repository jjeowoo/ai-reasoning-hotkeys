using System;
using System.Drawing;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;

internal static class StrokesImageSearch
{
    private static MethodInfo matcher;
    private static Bitmap preparedNeedle, searchSeed;
    private static Point seedOffset;
    private static int matchCount, wholeCaptures, localCaptures;
    private static long matchMilliseconds, captureMilliseconds;
    internal static string Summary
    {
        get { return "Image matches=" + matchCount + ", time=" + matchMilliseconds +
            " ms; captures whole=" + wholeCaptures + ", local=" + localCaptures + ", time=" + captureMilliseconds + " ms."; }
    }
    internal static void RecordCapture(bool fullWindow, long milliseconds)
    {
        if (fullWindow) wholeCaptures++; else localCaptures++;
        captureMilliseconds += milliseconds;
    }

    private static object RawMatch(Bitmap needle, Bitmap haystack)
    {
        if (matcher == null)
        {
            string install = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "StrokesPlus.net");
            Assembly assembly = Assembly.LoadFrom(Path.Combine(install, "StrokesPlus.net.exe"));
            Type images = assembly.GetType("StrokesPlus.net.Engine.StrokesPlusClasses+Multimedia+Images", true);
            matcher = images.GetMethod("FindImageWithinImage", new[] { typeof(Image), typeof(Image) });
            if (matcher == null) throw new InvalidOperationException("StrokesPlus image matching function is unavailable.");
        }
        return matcher.Invoke(null, new object[] { needle, haystack });
    }

    private static void Prepare(Bitmap needle)
    {
        if (Object.ReferenceEquals(needle, preparedNeedle)) return;
        if (searchSeed != null) { searchSeed.Dispose(); searchSeed = null; }
        preparedNeedle = needle;
        // A first row that already contains text needs no extra search key.
        int firstPixel = needle.GetPixel(0, 0).ToArgb();
        for (int x = 1; x < needle.Width; x++)
            if (needle.GetPixel(x, 0).ToArgb() != firstPixel) return;
        // The Claude anchor starts with a white row. S+ otherwise enumerates
        // every matching white strip before checking the text below it.
        for (int y = 0; y < needle.Height - 2; y++)
            for (int x = 0; x < needle.Width - 15; x++)
            {
                Color color = needle.GetPixel(x, y);
                if (color.R >= 225 || color.G >= 225 || color.B >= 225) continue;
                if (x == 0 && y == 0) return;
                seedOffset = new Point(x, y);
                searchSeed = needle.Clone(new Rectangle(x, y, Math.Min(80, needle.Width - x),
                    needle.Height - y), PixelFormat.Format24bppRgb);
                return;
            }
    }

    private static bool ExactMatchAt(Bitmap needle, Bitmap haystack, Point point)
    {
        if (point.X < 0 || point.Y < 0 || point.X + needle.Width > haystack.Width ||
            point.Y + needle.Height > haystack.Height) return false;
        for (int y = 0; y < needle.Height; y++)
            for (int x = 0; x < needle.Width; x++)
                if (needle.GetPixel(x, y).ToArgb() != haystack.GetPixel(point.X + x, point.Y + y).ToArgb())
                    return false;
        return true;
    }

    internal static object Match(Bitmap needle, Bitmap haystack)
    {
        Stopwatch timer = Stopwatch.StartNew();
        matchCount++;
        try
        {
            // Tiny popup crops already search quickly with the original matcher.
            if ((long)haystack.Width * haystack.Height <= 200000) return RawMatch(needle, haystack);
            Prepare(needle);
            if (searchSeed == null) return RawMatch(needle, haystack);
            object match = RawMatch(searchSeed, haystack);
            if (match == null) return null;
            Point candidate = (Point)match;
            candidate.Offset(-seedOffset.X, -seedOffset.Y);
            // The smaller search key is only an accelerator. Every original pixel
            // must still agree before returning coordinates for a click.
            if (ExactMatchAt(needle, haystack, candidate)) return candidate;
            // An identical text fragment elsewhere must not hide the real popup.
            return RawMatch(needle, haystack);
        }
        finally { matchMilliseconds += timer.ElapsedMilliseconds; }
    }

    internal static void SelfTest()
    {
        using (Image reference = Image.FromFile(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Claude-effort-reference.png")))
        using (Bitmap needle = new Bitmap(196, 14, PixelFormat.Format24bppRgb))
        using (Bitmap canvas = new Bitmap(800, 400, PixelFormat.Format24bppRgb))
        {
            using (Graphics graphics = Graphics.FromImage(needle))
                graphics.DrawImage(reference, new Rectangle(0, 0, 196, 14), new Rectangle(15, 61, 196, 14), GraphicsUnit.Pixel);
            using (Graphics graphics = Graphics.FromImage(canvas))
            {
                graphics.Clear(Color.White);
                graphics.DrawImageUnscaled(reference, 240, 100);
            }
            object found = Match(needle, canvas);
            if (found == null || (Point)found != new Point(255, 161))
                throw new InvalidOperationException("Seeded image search lost the full popup anchor.");
            Prepare(needle);
            if (searchSeed == null) throw new InvalidOperationException("Claude text search seed was not prepared.");
            // Put the same seed earlier in the image, but omit the rest of the
            // original anchor. The full matcher must recover the later popup.
            using (Graphics graphics = Graphics.FromImage(canvas))
                graphics.DrawImageUnscaled(searchSeed, 20 + seedOffset.X, 20 + seedOffset.Y);
            found = Match(needle, canvas);
            if (found == null || (Point)found != new Point(255, 161))
                throw new InvalidOperationException("Partial text match hid the real popup.");
            using (Graphics graphics = Graphics.FromImage(canvas))
            {
                graphics.Clear(Color.White);
                graphics.DrawImageUnscaled(searchSeed, 20 + seedOffset.X, 20 + seedOffset.Y);
            }
            if (Match(needle, canvas) != null)
                throw new InvalidOperationException("Partial anchor was accepted as a popup.");
            using (Graphics graphics = Graphics.FromImage(canvas)) graphics.Clear(Color.White);
            if (Match(needle, canvas) != null)
                throw new InvalidOperationException("Empty white canvas was accepted as a popup.");
        }
    }
}