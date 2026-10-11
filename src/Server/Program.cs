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
            for (int i = 0; i < args.Length; i++)
            {
                if ((args[i] == "--port" || args[i] == "-p") && i + 1 < args.Length && int.TryParse(args[i + 1], out int p)) port = p;
                else if (args[i] == "--help" || args[i] == "-h")
                {
                    Console.WriteLine("DigimonNetplayServer [--port 7777]");
                    Console.WriteLine("Relay/lobby server for the Project Digimon online versus mod. Forward this TCP port if players connect over the internet.");
                    return 0;
                }
            }
            var env = Environment.GetEnvironmentVariable("PORT");
            if (env != null && int.TryParse(env, out int ep)) port = ep;

            var server = new RelayServer { Log = m => Console.WriteLine(DateTime.Now.ToString("HH:mm:ss ") + m) };
            server.Start(port);
            Console.WriteLine("Press Ctrl+C to stop.");
            var done = new ManualResetEvent(false);
            Console.CancelKeyPress += (s, e) => { e.Cancel = true; done.Set(); };
            done.WaitOne();
            server.Stop();
            return 0;
        }
    }
}
