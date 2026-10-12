using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using DigimonNetplay.Shared;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace DigimonNetplay
{
    /// <summary>Everything that touches the game's own classes lives here.</summary>
    internal static class GameBridge
    {
        public sealed class CharOption
        {
            public string Name;
            public GameData.DigimonClass Class;
            public List<int> Colors = new List<int>();          // 1-based, as the game stores them
            public List<KeyValuePair<int, string>> Evolutions = new List<KeyValuePair<int, string>>(); // alt form (1-based) -> name
        }

        public static readonly Dictionary<GameData.STAGES, string> StageScenes = new Dictionary<GameData.STAGES, string>
        {
            { GameData.STAGES.TRI_BRIDGE, "Stage-BridgeTri" },
            { GameData.STAGES.YGGDRASIL_ROOM, "Stage-YggdrasilsRoom" },
            { GameData.STAGES.CITY_RUINS, "Stage-RuinedCity" },
            { GameData.STAGES.DANGEROUS_FOREST, "Stage-DangerousForest" },
            { GameData.STAGES.DANGEROUS_FOREST_ALT, "Stage-DangerousForest_Alt" },
            { GameData.STAGES.FREEZY_ARCTIC, "Stage-FreezyArtic" },
            { GameData.STAGES.FREEZY_ARCTIC_ALT, "Stage-FreezyArtic_Alt" },
            { GameData.STAGES.SKY_SANCTUARY, "Stage-SkySanctuary" },
            { GameData.STAGES.BURNING_VOLCANO, "Stage-BurningVolcano" },
            { GameData.STAGES.MECHA_FACTORY, "Stage-MechaFactory" },
            { GameData.STAGES.REVOLUTION, "Stage-Revolution" },
            { GameData.STAGES.TRAILMON_STATION, "Stage-TrailmonStation" },
            { GameData.STAGES.SERVER_DESERT, "Stage-ServerDesert" },
            { GameData.STAGES.DREAPER_ZONE, "Stage-DReaperZone" },
            { GameData.STAGES.REAPERMONS_DEN, "Stage-ReapermonDen" },
            { GameData.STAGES.UNIDENTIFIED_NETWORK, "Stage-UnidentifiedNetwork" },
            { GameData.STAGES.DARK_AREA, "Stage-DarkArea" },
            { GameData.STAGES.CHAOS_LORDS_DOMAIN, "Stage-ChaosLordsDomain" },
            { GameData.STAGES.INNER_HAZARD, "Stage-InnerHazard" },
            { GameData.STAGES.INNER_HAZARD_ALT, "Stage-InnerHazard_Alt" },
            { GameData.STAGES.THRONE_OF_PRIDE, "Stage-LucemonsThrone" },
            { GameData.STAGES.GUNSLINGER, "Stage-Gunslinger" },
            { GameData.STAGES.STAGE_SATELLITE, "Stage-SpaceSatellite" },
            { GameData.STAGES.STEAMWORKS, "Stage-Steamworks" },
            { GameData.STAGES.END_OF_AN_ERA, "Stage-EndOfAnEra" },
            { GameData.STAGES.PARALLEL_DIMENSION, "Stage-ParallelDimension" },
            { GameData.STAGES.OLYMPIA, "Stage-Olympia" },
            { GameData.STAGES.OLYMPIA_ALT, "Stage-Olympia_Alt" },
            { GameData.STAGES.COSMIC_CONVERGENCE, "Stage-CosmicConvergence" },
            { GameData.STAGES.CHARISMA_CASTLE, "Stage-CharismaCastle" },
        };

        public static bool IsStageScene(string sceneName) => sceneName != null && sceneName.StartsWith("Stage-");

        public static bool GameReady => GameData.Game != null && GameData.Game.m_charactersNEW != null && GameData.Game.m_charactersNEW.Any(c => c != null);

        public static bool InMenus
        {
            get
            {
                string s = SceneManager.GetActiveScene().name;
                return s.StartsWith("Menu-") && s != "Menu-Loading";
            }
        }

        // ------------------------------------------------------------------ lobby data

        /// <summary>Characters unlocked in this player's own save.</summary>
        public static List<CharOption> UnlockedCharacters()
        {
            var list = new List<CharOption>();
            if (!GameReady) return list;
            foreach (var c in GameData.Game.m_charactersNEW)
            {
                if (c == null || string.IsNullOrEmpty(c.Name) || !c.IsUnlocked) continue;
                var opt = new CharOption { Name = c.Name, Class = c.Class };
                if (c.ColorsUnlocked != null)
                    for (int i = 0; i < c.ColorsUnlocked.Length; i++) if (c.ColorsUnlocked[i]) opt.Colors.Add(i + 1);
                if (opt.Colors.Count == 0) opt.Colors.Add(1);
                if (c.HasDigivolution && c.Digivolutions != null)
                    for (int i = 0; i < c.Digivolutions.Length; i++)
                    {
                        var d = c.Digivolutions[i];
                        if (d != null && !string.IsNullOrEmpty(d.DigivolutionName) && d.IsUnlocked)
                            opt.Evolutions.Add(new KeyValuePair<int, string>(i + 1, d.DigivolutionName));
                    }
                list.Add(opt);
            }
            return list;
        }

        public static List<GameData.STAGES> UnlockedStages()
        {
            var list = new List<GameData.STAGES>();
            if (GameData.Game?.Stages == null) return list;
            foreach (var s in GameData.Game.Stages)
            {
                if (s == null || !s.IsUnlocked || !StageScenes.ContainsKey(s.StageValue)) continue;
                list.Add(s.StageValue);
                if (s.HasAltVersion && s.AltStages != null)
                    foreach (var a in s.AltStages)
                        if (a != null && a.IsUnlocked && StageScenes.ContainsKey(a.StageValue)) list.Add(a.StageValue);
            }
            return list;
        }

        public static string StageLabel(int stage)
        {
            if (stage == (int)GameData.STAGES._RANDOM_) return "Random";
            var v = (GameData.STAGES)stage;
            var data = GameData.Game?.Stages?.FirstOrDefault(s => s.StageValue == v);
            if (data != null && !string.IsNullOrEmpty(data.StageName)) return data.StageName;
            var alt = GameData.Game?.Stages?.SelectMany(s => s.AltStages ?? new List<GameData.AltStageData>()).FirstOrDefault(a => a.StageValue == v);
            if (alt != null && !string.IsNullOrEmpty(alt.StageName)) return alt.StageName;
            return v.ToString().Replace('_', ' ');
        }

        public static GameData.DigimonClass ClassOf(string character)
        {
            var c = GameData.Game?.m_charactersNEW?.FirstOrDefault(x => x != null && x.Name == character);
            return c?.Class ?? GameData.DigimonClass.UNDEFINED;
        }

        public static bool CharacterExists(string character) =>
            GameData.Game?.m_charactersNEW?.Any(x => x != null && x.Name == character) ?? false;

        // ------------------------------------------------------------------ host settings that change the battle

        private static readonly string[] SettingFields =
        {
            "BattleRounds", "ItemsEnabled", "HazardsEnabled", "TimingIsEnabled", "DamageHandicap", "AllowStageUltraModes", "Difficulty",
        };

        private static IEnumerable<FieldInfo> SyncedFields()
        {
            var t = typeof(GameData.Data);
            foreach (var n in SettingFields)
            {
                var f = t.GetField(n);
                if (f != null) yield return f;
            }
            // Special colour/form toggles change which model and moveset a fighter loads.
            foreach (var f in t.GetFields())
                if (f.FieldType == typeof(bool) && f.Name.StartsWith("IsForm") && !f.Name.EndsWith("_Obtained") && !f.Name.EndsWith("_AlreadyUsed"))
                    yield return f;
        }

        public static Dictionary<string, string> CaptureSettings()
        {
            var d = new Dictionary<string, string>();
            foreach (var f in SyncedFields())
                d[f.Name] = Convert.ToString(f.GetValue(GameData.Game), CultureInfo.InvariantCulture);
            d["GAME_SPEED"] = GameData.GAME_SPEED.ToString("R", CultureInfo.InvariantCulture);
            return d;
        }

        public static void ApplySettings(Dictionary<string, string> settings)
        {
            var t = typeof(GameData.Data);
            foreach (var kv in settings)
            {
                try
                {
                    if (kv.Key == "GAME_SPEED") { GameData.GAME_SPEED = float.Parse(kv.Value, CultureInfo.InvariantCulture); continue; }
                    var f = t.GetField(kv.Key);
                    if (f == null) continue;
                    f.SetValue(GameData.Game, Convert.ChangeType(kv.Value, f.FieldType, CultureInfo.InvariantCulture));
                }
                catch (Exception e) { Plugin.Log.LogWarning("Could not apply setting " + kv.Key + ": " + e.Message); }
            }
        }

        // ------------------------------------------------------------------ starting a battle

        private static string[] _savedControl, _savedGamepad;
        private static Dictionary<string, string> _savedSettings;

        public static void SaveLocalState()
        {
            _savedControl = (string[])GameData.CONTROL.Clone();
            _savedGamepad = (string[])GameData.CONTROL_GAMEPAD.Clone();
            _savedSettings = CaptureSettings();
        }

        public static void RestoreLocalState()
        {
            if (_savedControl != null) GameData.CONTROL = _savedControl;
            if (_savedGamepad != null) GameData.CONTROL_GAMEPAD = _savedGamepad;
            if (_savedSettings != null) ApplySettings(_savedSettings);
            _savedControl = null; _savedGamepad = null; _savedSettings = null;
        }

        /// <summary>The player's own settings, swapped in while the game writes its save file.</summary>
        public static Dictionary<string, string> SavedSettings => _savedSettings;

        /// <summary>Fills the same statics the character select screen fills, then opens the VS loading screen.</summary>
        public static void LaunchBattle(MatchConfig cfg)
        {
            int n = cfg.PlayerCount;
            bool royale = cfg.BattleRoyale;
            int rows = royale ? 4 : 2;

            ApplySettings(cfg.Settings);

            var controls = new string[4] { "N0", "N1", "N2", "N3" };
            GameData.CONTROL = controls;
            GameData.CONTROL_GAMEPAD = new string[4] { "0", "1", "2", "3" };

            Menu_Loading.IS_VERSUS_MODE = !royale;
            Menu_Loading.IS_BATTLE_ROYALE_4P = royale;
            Menu_Loading.IS_TEAM_BATTLE = false;
            Menu_Loading.IS_ARCADE_MODE = false;
            Menu_Loading.IS_MEGA_ARCADE = false;
            Menu_Loading.IS_SPECIAL_BATTLE = false;
            Menu_Loading.IS_SURVIVAL = false;
            Menu_Loading.IS_TIMEATTACK = false;
            Menu_Loading.IS_SPECTATOR_MODE = false;
            Menu_Loading.IS_BATTLE_AGAINST_MILLENNIUMMON = false;
            Menu_Loading.SPECIAL_BATTLE_NUM = -1;
            Menu_Loading.SPECIAL_BATTLE_IGNORE_DIGIMON_SELECTED = false;
            Menu_Loading.SPECIAL_BATTLE_IS_LOADING_SCENE = false;
            Menu_Loading.SPECIAL_BATTLE_IS_BATTLE_SCENE = false;
            Menu_Loading.ARCADE_CURRENT_BATTLE = 0;
            Menu_Loading.CAN_SWITCH = new bool[2];
            Menu_CharSelection.IS_VERSUS_MODE = !royale;
            Menu_CharSelection.IS_BATTLE_ROYALE_4P = royale;
            Menu_CharSelection.IS_TEAM_BATTLE = false;
            Menu_CharSelection.IS_ARCADE_MODE = false;
            Menu_CharSelection.IS_SPECIAL_BATTLE = false;
            Menu_CharSelection.IS_SURVIVAL = false;
            Menu_CharSelection.IS_TIMEATTACK = false;
            Menu_CharSelection.IS_SPECTATOR_MODE = false;
            Menu_CharSelection.IS_BATTLE_AGAINST_MILLENNIUMMON = false;
            Menu_CharSelection.ReturningFromOtherBattle = false;
            Menu_CharSelection.VICTORIES_VS = new int[2];

            var players = new string[rows, 1];
            var colors = new int[rows, 1];
            var alts = new int[rows, 1];
            var types = new GameData.DigimonClass[rows, 1];
            var cpu = new bool[rows, 1];
            var diff = new int[rows, 1];
            var ids = new int[rows, 1];
            for (int i = 0; i < rows; i++)
            {
                players[i, 0] = i < n ? cfg.Picks[i].Character : "";
                colors[i, 0] = i < n ? cfg.Picks[i].Color : -1;
                alts[i, 0] = i < n ? cfg.Picks[i].AltForm : 1;
                types[i, 0] = i < n ? ClassOf(cfg.Picks[i].Character) : GameData.DigimonClass.UNDEFINED;
                ids[i, 0] = -1;
                diff[i, 0] = GameData.Game.Difficulty;
            }
            Menu_Loading.PLAYERS = players;
            Menu_Loading.PLAYERS_COLOR = colors;
            Menu_Loading.ALT_FORMS = alts;
            Menu_Loading.PLAYERS_TYPES = types;
            Menu_Loading.PLAYERS_CPU = cpu;
            Menu_Loading.DIFFICULTIES = diff;
            Menu_Loading.CHARS_ID = ids;
            if (royale)
            {
                Menu_Loading.BATTLE_ROYALE_TEAMS = new int[4] { 1, 2, 3, 4 };
                Menu_Loading.BATTLE_ROYALE_HANDICAP = new float[4] { 1f, 1f, 1f, 1f };
            }
            Menu_Loading.STAGE = (GameData.STAGES)cfg.Stage;
            Menu_Loading.SONG_NUMBER = cfg.Song;
            Menu_Loading.SONG_NAME = "";
            Menu_Loading.LEVEL_TO_LOAD = "";
            Gameplay.BattleEndedByTimeover = false;
            Gameplay.isPaused = false;

            Time.timeScale = 1f;
            SceneManager.LoadScene("Menu-Loading");
        }

        /// <summary>Leave a broken match: back to the main menu through the loading screen.</summary>
        public static void BailToMainMenu()
        {
            Menu_Loading.PLAYERS = new string[2, 1] { { "" }, { "" } };
            Menu_Loading.SPECIAL_BATTLE_IS_LOADING_SCENE = false;
            Menu_Loading.SPECIAL_BATTLE_IGNORE_DIGIMON_SELECTED = false;
            Menu_Loading.IS_ARCADE_MODE = false;
            Menu_Loading.LEVEL_TO_LOAD = "Menu-Main";
            Menu_Main.ReturningFromOtherMenus = true;
            Gameplay.isPaused = false;
            Time.timeScale = 1f;
            SceneManager.LoadScene("Menu-Loading");
        }

        /// <summary>The network slot that won the battle that just ended, or -1 (draw, time over, unknown).</summary>
        public static int ReadWinner(MatchConfig cfg)
        {
            var wins = Gameplay.TEAM_ROUND_VICTORIES;
            int need = Gameplay.ROUNDS_TO_WIN;
            if (wins == null || need <= 0) return -1;
            int winner = -1;
            for (int i = 0; i < cfg.PlayerCount && i < wins.Length; i++)
            {
                if (wins[i] < need) continue;
                if (winner >= 0) return -1;
                winner = i;
            }
            return winner < 0 ? -1 : cfg.Slots[winner];
        }

        // ------------------------------------------------------------------ local input

        private static readonly string[] Buttons = { "Attack", "Special", "Jump", "Protect", "Evolve", "Grab", "Crouch", "R", "L" };

        /// <summary>Reads this player's real controller through the game's own bindings (keyboard and/or gamepad).</summary>
        public static PadState SampleLocal(string[] controls)
        {
            float h = 0, v = 0, c = 0;
            ushort bits = 0;
            foreach (var ctl in controls)
            {
                try
                {
                    h = Stronger(h, InputController.GetAxis("Horizontal" + ctl));
                    v = Stronger(v, InputController.GetAxis("Vertical" + ctl));
                    c = Stronger(c, InputController.GetAxis("Crouch" + ctl));
                    foreach (var b in Buttons)
                        if (InputController.GetButton(b + ctl)) bits |= PadState.BitFor(b);
                }
                catch (Exception) { /* control set not present on this machine */ }
            }
            return new PadState { H = PadState.FloatToAxis(h), V = PadState.FloatToAxis(v), CrouchAxis = PadState.FloatToAxis(c), Buttons = bits };
        }

        private static float Stronger(float a, float b) => Mathf.Abs(b) > Mathf.Abs(a) ? b : a;

        // ------------------------------------------------------------------ desync checks

        private static readonly FieldInfo HealthField = AccessTools.Field(typeof(StageController), "PlayerHealth");
        private static readonly FieldInfo EnergyField = AccessTools.Field(typeof(StageController), "PlayerEnergy");
        private static StageController _stage;

        private static Image[] Bars(FieldInfo f)
        {
            if (_stage == null) _stage = UnityEngine.Object.FindObjectOfType<StageController>();
            return _stage == null || f == null ? null : f.GetValue(_stage) as Image[];
        }

        private static List<Controller> ActiveFighters()
        {
            var list = UnityEngine.Object.FindObjectsOfType<Controller>().Where(c => c != null && c.isActiveAndEnabled && c.GetPlayerNum() > 0).ToList();
            list.Sort((a, b) => a.GetPlayerNum() != b.GetPlayerNum() ? a.GetPlayerNum().CompareTo(b.GetPlayerNum()) : string.CompareOrdinal(a.name, b.name));
            return list;
        }

        public static List<StateEntry> CaptureState()
        {
            var list = new List<StateEntry>();
            var health = Bars(HealthField);
            var energy = Bars(EnergyField);
            foreach (var c in ActiveFighters())
            {
                int p = c.GetPlayerNum() - 1;
                var pos = c.transform.position;
                list.Add(new StateEntry
                {
                    Key = "P" + p + ":" + c.name,
                    X = pos.x, Y = pos.y, Z = pos.z,
                    Health = health != null && p < health.Length && health[p] != null ? health[p].fillAmount : 0f,
                    Energy = energy != null && p < energy.Length && energy[p] != null ? energy[p].fillAmount : 0f,
                });
            }
            return list;
        }

        /// <summary>Nudge fighters and life/energy bars by the host's offsets.</summary>
        public static void ApplyCorrection(List<StateEntry> deltas)
        {
            var health = Bars(HealthField);
            var energy = Bars(EnergyField);
            var fighters = ActiveFighters().ToDictionary(c => "P" + (c.GetPlayerNum() - 1) + ":" + c.name);
            var barsDone = new HashSet<int>();
            foreach (var d in deltas)
            {
                if (!fighters.TryGetValue(d.Key, out var c)) continue;
                c.transform.position += new Vector3(d.X, d.Y, d.Z);
                int p = c.GetPlayerNum() - 1;
                if (!barsDone.Add(p)) continue;
                if (health != null && p < health.Length && health[p] != null) health[p].fillAmount = Mathf.Clamp01(health[p].fillAmount + d.Health);
                if (energy != null && p < energy.Length && energy[p] != null) energy[p].fillAmount = Mathf.Clamp01(energy[p].fillAmount + d.Energy);
            }
            Physics.SyncTransforms();
        }

        public static void ForgetScene() => _stage = null;

        /// <summary>Animations that stop when off camera depend on each player's screen size; keep them all running.</summary>
        public static void ForceAlwaysAnimate()
        {
            foreach (var a in UnityEngine.Object.FindObjectsOfType<Animation>()) a.cullingType = AnimationCullingType.AlwaysAnimate;
            foreach (var a in UnityEngine.Object.FindObjectsOfType<Animator>()) a.cullingMode = AnimatorCullingMode.AlwaysAnimate;
        }
    }
}
