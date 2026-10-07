using System;
using System.Drawing;
using System.IO;
using System.Reflection;

internal static class StrokesImageSearch
{
    private static MethodInfo matcher;
    internal static object Match(Bitmap needle, Bitmap haystack)
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
}
