using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using BepInEx;
using DigimonNetplay.Shared;
using UnityEngine;

namespace DigimonNetplay
{
    /// <summary>Builds a problem report: system info, this mod's settings and the end of the BepInEx log, with personal details removed.</summary>
    internal static class ProblemReport
    {
        private const int LogTail = 512 * 1024;
        private static readonly Regex Ipv4 = new Regex(@"\b(?!127\.0\.0\.1\b)(?!0\.0\.0\.0\b)(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})\b", RegexOptions.Compiled);

        public static string Build(string description, string server)
        {
            var sb = new StringBuilder();
            sb.Append("Digimon Netplay ").Append(BuildInfo.Version).Append(" problem report\n");
            sb.Append("Created: ").Append(DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss")).Append(" UTC\n");
            sb.Append("Description: ").Append(description).Append('\n');
            sb.Append("Game version: ").Append(Application.version).Append(", Unity ").Append(Application.unityVersion).Append('\n');
            sb.Append("System: ").Append(SystemInfo.operatingSystem).Append(", ").Append(SystemInfo.processorType).Append(", ")
              .Append(SystemInfo.systemMemorySize).Append(" MB RAM, ").Append(SystemInfo.graphicsDeviceName).Append('\n');
            sb.Append("Screen: ").Append(Screen.width).Append('x').Append(Screen.height).Append(" @ ").Append(Screen.currentResolution.refreshRate).Append(" Hz\n");
            sb.Append("Lobby server: ").Append(server).Append('\n');
            sb.Append("Direct connections allowed: ").Append(Plugin.AllowP2P.Value).Append(", controls: ").Append(Plugin.LocalControls.Value)
              .Append(", stall timeout: ").Append(Plugin.StallTimeoutSeconds.Value).Append(" s\n");
            sb.Append("Plugins: ");
            foreach (var p in BepInEx.Bootstrap.Chainloader.PluginInfos.Values) sb.Append(p.Metadata.Name).Append(' ').Append(p.Metadata.Version).Append("; ");
            sb.Append("\n\n----- BepInEx log (last ").Append(LogTail / 1024).Append(" KB) -----\n");
            sb.Append(ReadLogTail());
            return Scrub(sb.ToString());
        }

        private static string ReadLogTail()
        {
            string path = Path.Combine(Paths.BepInExRootPath, "LogOutput.log");
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    long start = Math.Max(0, fs.Length - LogTail);
                    fs.Seek(start, SeekOrigin.Begin);
                    var buf = new byte[fs.Length - start];
                    int got = 0;
                    while (got < buf.Length)
                    {
                        int n = fs.Read(buf, got, buf.Length - got);
                        if (n <= 0) break;
                        got += n;
                    }
                    return Encoding.UTF8.GetString(buf, 0, got);
                }
            }
            catch (Exception e)
            {
                return "(could not read " + Path.GetFileName(path) + ": " + e.Message + ")";
            }
        }

        /// <summary>Removes the Windows user name, home folder and IP addresses.</summary>
        public static string Scrub(string text)
        {
            try
            {
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (!string.IsNullOrEmpty(home) && home.Length > 3)
                {
                    text = ReplaceIgnoreCase(text, home, "<home>");
                    text = ReplaceIgnoreCase(text, home.Replace('\\', '/'), "<home>");
                }
                string user = Environment.UserName;
                if (!string.IsNullOrEmpty(user) && user.Length >= 3) text = Regex.Replace(text, @"\b" + Regex.Escape(user) + @"\b", "<user>", RegexOptions.IgnoreCase);
                string machine = Environment.MachineName;
                if (!string.IsNullOrEmpty(machine) && machine.Length >= 3) text = Regex.Replace(text, @"\b" + Regex.Escape(machine) + @"\b", "<pc>", RegexOptions.IgnoreCase);
            }
            catch { }
            return Ipv4.Replace(text, "x.x.x.x");
        }

        private static string ReplaceIgnoreCase(string text, string find, string with)
        {
            int i;
            while ((i = text.IndexOf(find, StringComparison.OrdinalIgnoreCase)) >= 0) text = text.Substring(0, i) + with + text.Substring(i + find.Length);
            return text;
        }

        public static byte[] Gzip(string text)
        {
            using (var ms = new MemoryStream())
            {
                using (var z = new GZipStream(ms, CompressionMode.Compress))
                {
                    var b = Encoding.UTF8.GetBytes(text);
                    z.Write(b, 0, b.Length);
                }
                return ms.ToArray();
            }
        }

        /// <summary>Saves the report next to the game, for when the server cannot take it. Returns the file path.</summary>
        public static string SaveLocally(string report)
        {
            string path = Path.Combine(Paths.GameRootPath, "NetplayReport-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt");
            File.WriteAllText(path, report);
            return path;
        }
    }

    /// <summary>The anonymous ranked account: a random secret kept in its own file, never in the shared config.</summary>
    internal static class Account
    {
        public static string LoadOrCreateToken()
        {
            string path = Path.Combine(Paths.ConfigPath, "DigimonNetplay.account");
            try
            {
                if (File.Exists(path))
                {
                    string t = File.ReadAllText(path).Trim();
                    if (t.Length >= 32) return t;
                }
                var bytes = new byte[32];
                using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
                var sb = new StringBuilder();
                foreach (byte b in bytes) sb.Append(b.ToString("x2"));
                File.WriteAllText(path, sb.ToString());
                Plugin.Log.LogInfo("Created a ranked account key (" + Path.GetFileName(path) + "). Keep it to keep your rating.");
                return sb.ToString();
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Could not read or create the ranked account key: " + e.Message);
                return "";
            }
        }
    }
}
