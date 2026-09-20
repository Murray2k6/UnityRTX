using System;

namespace UnityRemix
{
    internal static class TextureCaptureSize
    {
        public static (int width, int height) Limit(int width, int height, int maximum)
        {
            if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
            int longest = Math.Max(width, height);
            if (maximum <= 0 || longest <= maximum) return (width, height);
            return (Math.Max(1, (int)((long)width * maximum / longest)),
                Math.Max(1, (int)((long)height * maximum / longest)));
        }
    }
}
