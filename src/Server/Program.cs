using System;
using System.Threading;
using DigimonNetplay.Shared;

namespace DigimonNetplay.Server
{
    public static class Program
    {
        public static int Main(string[] args)
        {
            int port = 7777;
            var server = new RelayServer { Log = m => Console.WriteLine(DateTime.Now.ToString("HH:mm:ss ") + m) };
            for (int i = 0; i < args.Length; i++)
            {
                string next = i + 1 < args.Length ? args[i + 1] : null;
                switch (args[i])
                {
                    case "--port": case "-p":
                        if (int.TryParse(next, out int p)) { port = p; i++; }
                        break;
                    case "--name":
                        if (next != null) { server.ServerName = next; i++; }
                        break;
                    case "--behind-proxy":
                        server.BehindProxy = true;
                        break;
                    case "--max-clients":
                        if (int.TryParse(next, out int mc)) { server.MaxClients = mc; i++; }
                        break;
                    case "--data-dir":
                        if (next != null) { server.DataDir = next; i++; }
                        break;
                    case "--download-url":
                        if (next != null) { server.DownloadUrl = next; i++; }
                        break;
                    case "--latest-version":
                        if (next != null) { server.LatestVersion = next; i++; }
                        break;
                    case "--max-rooms":
                        if (int.TryParse(next, out int mr)) { server.MaxRooms = mr; i++; }
                        break;
                    case "--help": case "-h":
                        Console.WriteLine("DigimonNetplayServer [--port 7777] [--name \"My server\"] [--behind-proxy] [--data-dir ./data]");
                        Console.WriteLine("                     [--max-clients 1000] [--max-rooms 300] [--latest-version " + BuildInfo.Version + "] [--download-url URL]");
                        Console.WriteLine();
                        Console.WriteLine("Lobby and relay server for the Project Digimon online versus mod.");
                        Console.WriteLine("The port accepts the game directly (TCP), WebSocket connections (for Cloudflare or another");
                        Console.WriteLine("HTTPS proxy in front) and serves a status page at http://<host>:<port>/status.");
                        Console.WriteLine("Ranked leaderboard: http://<host>:<port>/leaderboard (and /leaderboard.json).");
                        Console.WriteLine();
                        Console.WriteLine("--behind-proxy    trust the proxy's CF-Connecting-IP / X-Forwarded-For header for per-address limits.");
                        Console.WriteLine("--data-dir        where ranked ratings (ratings.tsv) and problem reports (reports/) are kept. Default: ./data");
                        Console.WriteLine("--latest-version  newest mod version; players with an older one are told to update. Default: this server's version.");
                        Console.WriteLine("--download-url    where players get the mod. Default: " + BuildInfo.ReleasesUrl);
                        return 0;
                }
            }
            if (server.DataDir == null) server.DataDir = "data";
            var env = Environment.GetEnvironmentVariable("PORT");
            if (env != null && int.TryParse(env, out int ep)) port = ep;

            server.Start(port);
            Console.WriteLine("Digimon Netplay server " + BuildInfo.Version + ", leaderboard at http://localhost:" + server.Port + "/leaderboard");
            Console.WriteLine("Server name: " + server.ServerName + (server.BehindProxy ? " (behind a proxy)" : ""));
            Console.WriteLine("Press Ctrl+C to stop.");
            var done = new ManualResetEvent(false);
            Console.CancelKeyPress += (s, e) => { e.Cancel = true; done.Set(); };
            AppDomain.CurrentDomain.ProcessExit += (s, e) => done.Set(); // systemd stop
            done.WaitOne();
            server.Stop();
            return 0;
        }
    }
}
