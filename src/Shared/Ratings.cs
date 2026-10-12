using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace DigimonNetplay.Shared
{
    /// <summary>A ranked player. Players are anonymous: the key is a hash of a random secret their game made up.</summary>
    public sealed class PlayerRecord
    {
        public string Key, Name = "Tamer";
        public int Rating = RatingStore.StartRating, Wins, Losses, Draws;
        public long LastPlayed; // unix seconds

        public int Games => Wins + Losses + Draws;
        public string Tag => Key.Substring(0, 4).ToUpperInvariant();
        public string Display => Name + "#" + Tag;
    }

    /// <summary>Elo ratings, kept in a small tab-separated file next to the server.</summary>
    public sealed class RatingStore
    {
        public const int StartRating = 1000;

        private readonly object _lock = new object();
        private readonly Dictionary<string, PlayerRecord> _players = new Dictionary<string, PlayerRecord>();
        private readonly string _path;
        public Action<string> Log = _ => { };

        /// <summary><paramref name="path"/> null keeps ratings in memory only (a server hosted inside the game).</summary>
        public RatingStore(string path)
        {
            _path = path;
            if (path == null || !File.Exists(path)) return;
            foreach (string line in File.ReadAllLines(path))
            {
                var f = line.Split('\t');
                if (f.Length < 7 || f[0].Length < 8) continue;
                try
                {
                    _players[f[0]] = new PlayerRecord
                    {
                        Key = f[0], Rating = int.Parse(f[1], CultureInfo.InvariantCulture), Wins = int.Parse(f[2], CultureInfo.InvariantCulture),
                        Losses = int.Parse(f[3], CultureInfo.InvariantCulture), Draws = int.Parse(f[4], CultureInfo.InvariantCulture),
                        LastPlayed = long.Parse(f[5], CultureInfo.InvariantCulture), Name = f[6],
                    };
                }
                catch (FormatException) { }
            }
        }

        public int Count { get { lock (_lock) return _players.Count; } }

        /// <summary>The account key for a client's secret token.</summary>
        public static string KeyOf(string token)
        {
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes("digimon-netplay:" + token));
                var sb = new StringBuilder();
                foreach (byte b in hash) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        /// <summary>Finds or creates the record for a key and remembers the latest name.</summary>
        public PlayerRecord Get(string key, string name)
        {
            lock (_lock)
            {
                if (!_players.TryGetValue(key, out var p)) _players[key] = p = new PlayerRecord { Key = key };
                if (!string.IsNullOrEmpty(name)) p.Name = name;
                return p;
            }
        }

        public static double Expected(int rating, int opponent) => 1.0 / (1.0 + Math.Pow(10, (opponent - rating) / 400.0));
        public static int KFactor(PlayerRecord p) => p.Games < 10 ? 40 : 32;

        /// <summary>Records a finished ranked game. <paramref name="scoreA"/>: 1 = a won, 0 = b won, 0.5 = draw.</summary>
        public void Apply(PlayerRecord a, PlayerRecord b, double scoreA)
        {
            lock (_lock)
            {
                double ea = Expected(a.Rating, b.Rating);
                int da = (int)Math.Round(KFactor(a) * (scoreA - ea));
                int db = (int)Math.Round(KFactor(b) * ((1 - scoreA) - (1 - ea)));
                a.Rating = Math.Max(100, a.Rating + da);
                b.Rating = Math.Max(100, b.Rating + db);
                if (scoreA > 0.75) { a.Wins++; b.Losses++; }
                else if (scoreA < 0.25) { a.Losses++; b.Wins++; }
                else { a.Draws++; b.Draws++; }
                a.LastPlayed = b.LastPlayed = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                Save();
            }
        }

        private List<PlayerRecord> Ranked() =>
            _players.Values.Where(p => p.Games > 0)
                .OrderByDescending(p => p.Rating).ThenByDescending(p => p.Wins).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();

        public List<LeaderRow> Top(int count)
        {
            lock (_lock)
                return Ranked().Take(count).Select((p, i) => new LeaderRow { Rank = i + 1, Name = p.Display, Rating = p.Rating, Wins = p.Wins, Losses = p.Losses }).ToList();
        }

        /// <summary>1-based place on the leaderboard, 0 before the first ranked game.</summary>
        public int RankOf(PlayerRecord p)
        {
            lock (_lock) return p.Games == 0 ? 0 : Ranked().IndexOf(p) + 1;
        }

        private void Save()
        {
            if (_path == null) return;
            try
            {
                var sb = new StringBuilder();
                foreach (var p in _players.Values.Where(x => x.Games > 0))
                    sb.Append(p.Key).Append('\t').Append(p.Rating.ToString(CultureInfo.InvariantCulture)).Append('\t')
                      .Append(p.Wins.ToString(CultureInfo.InvariantCulture)).Append('\t').Append(p.Losses.ToString(CultureInfo.InvariantCulture)).Append('\t')
                      .Append(p.Draws.ToString(CultureInfo.InvariantCulture)).Append('\t').Append(p.LastPlayed.ToString(CultureInfo.InvariantCulture)).Append('\t')
                      .Append(p.Name.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ')).Append('\n');
                string tmp = _path + ".tmp";
                File.WriteAllText(tmp, sb.ToString());
                if (File.Exists(_path)) File.Replace(tmp, _path, null);
                else File.Move(tmp, _path);
            }
            catch (Exception e) { Log("Could not save ratings: " + e.Message); }
        }
    }
}
