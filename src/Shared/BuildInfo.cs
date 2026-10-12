using System;

namespace DigimonNetplay.Shared
{
    /// <summary>Version of the mod and server, shared so the server can tell players about updates.</summary>
    public static class BuildInfo
    {
        public const string Version = "0.5.0";
        public const string ReleasesUrl = "https://github.com/DevCrashDay/ProjectDigimonOnline/releases/latest";

        /// <summary>True if <paramref name="a"/> is a newer dotted version than <paramref name="b"/> ("0.10.0" &gt; "0.9.2").</summary>
        public static bool IsNewer(string a, string b)
        {
            var x = Parse(a);
            var y = Parse(b);
            for (int i = 0; i < Math.Max(x.Length, y.Length); i++)
            {
                int p = i < x.Length ? x[i] : 0, q = i < y.Length ? y[i] : 0;
                if (p != q) return p > q;
            }
            return false;
        }

        private static int[] Parse(string v)
        {
            var parts = (v ?? "").Trim().TrimStart('v', 'V').Split('.');
            var n = new int[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                string digits = "";
                foreach (char ch in parts[i]) { if (char.IsDigit(ch)) digits += ch; else break; }
                int.TryParse(digits, out n[i]);
            }
            return n;
        }
    }
}
