using System;
using System.Collections;
using System.Diagnostics;
using System.Linq;
using DigimonNetplay.Shared;
using UnityEngine;
using UnityEngine.SceneManagement;
using Random = UnityEngine.Random;

namespace DigimonNetplay
{
    /// <summary>
    /// One online battle (all of its rounds). Owns the lockstep core and makes the game's frame loop
    /// deterministic while it runs: fixed frame time, synced inputs, seeded randomness, one physics step per frame.
    /// </summary>
    internal sealed class MatchSession
    {
        public const float FrameTime = 1f / 60f;

        public static MatchSession Current { get; private set; }

        public readonly LockstepCore Core;
        private readonly Action<byte[]> _broadcast;
        private readonly string[] _localControls;

        private PadState[] _pads, _prevPads, _scratch;
        private bool _frameOpen;          // inputs for Core.Frame were consumed and the frame is being simulated
        private bool _submittedThisFrame;
        private bool _ended;

        // pending scene switch
        private int _loadingSegment = -1;
        private string _loadingScene;
        private AsyncOperation _loadOp;
        private bool _activationRequested;

        // saved engine settings
        private int _savedTargetFps, _savedVSync;
        private bool _savedRunInBackground, _savedAutoSim;

        public double StallMs { get; private set; }

        // Wall-clock pacing: every match frame advances game time by exactly 1/60 s, so frames must also take 1/60 s
        // of real time. The game turns vSync back on whenever a fighter spawns (Manager.Awake), which on a 120/144 Hz
        // monitor ignores targetFrameRate and made matches run 2x+ too fast, so pacing is done here instead.
        private static readonly Stopwatch Clock = Stopwatch.StartNew();
        private double _nextFrameAt = -1;

        private void PaceFrame()
        {
            if (QualitySettings.vSyncCount != 0) QualitySettings.vSyncCount = 0;
            if (Application.targetFrameRate != -1) Application.targetFrameRate = -1;
            const double step = 1.0 / 60.0;
            double now = Clock.Elapsed.TotalSeconds;
            if (_nextFrameAt < 0 || now - _nextFrameAt > step * 4) _nextFrameAt = now; // first frame, or we fell behind (stall/hitch): don't race to catch up
            double wait = _nextFrameAt - now;
            if (wait > 0.002) System.Threading.Thread.Sleep((int)((wait - 0.0015) * 1000));
            while (Clock.Elapsed.TotalSeconds < _nextFrameAt) { }
            _nextFrameAt += step;
        }
        public string Status { get; private set; } = "Loading";

        private MatchSession(MatchConfig cfg, int localPlayer, bool isHost, Action<byte[]> broadcast, string[] localControls)
        {
            _broadcast = broadcast;
            _localControls = localControls;
            Core = new LockstepCore(cfg, localPlayer, isHost, broadcast) { Log = m => Plugin.Log.LogInfo(m) };
            int n = cfg.PlayerCount;
            _pads = new PadState[n];
            _prevPads = new PadState[n];
            _scratch = new PadState[n];
        }

        public static void Begin(MatchConfig cfg, int localPlayer, bool isHost, Action<byte[]> broadcast, string[] localControls)
        {
            Current?.End(null);
            var s = new MatchSession(cfg, localPlayer, isHost, broadcast, localControls);
            Current = s;
            NetClock.Reset();
            s.EnterDeterministicMode();
            GameBridge.SaveLocalState();
            Plugin.Log.LogInfo("Starting online match: " + string.Join(" vs ", cfg.Picks.Select(p => p.Character).ToArray()) + " on " + GameBridge.StageLabel(cfg.Stage) + ", delay " + cfg.InputDelay);
            GameBridge.LaunchBattle(cfg);
        }

        private void EnterDeterministicMode()
        {
            _savedTargetFps = Application.targetFrameRate;
            _savedVSync = QualitySettings.vSyncCount;
            _savedRunInBackground = Application.runInBackground;
            _savedAutoSim = Physics.autoSimulation;
            Application.runInBackground = true;   // an unfocused window must keep simulating or everyone stalls
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = 60;
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        private void LeaveDeterministicMode()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            Time.captureDeltaTime = 0f;
            Physics.autoSimulation = _savedAutoSim;
            Application.targetFrameRate = _savedTargetFps;
            QualitySettings.vSyncCount = _savedVSync;
            Application.runInBackground = _savedRunInBackground;
            NetClock.Active = false;
            FixedStepDriver.Suppress = false;
        }

        /// <summary>Normal finish (reason null) or abort.</summary>
        public void End(string abortReason)
        {
            if (_ended) return;
            _ended = true;
            Core.StopGating();
            if (abortReason != null) Core.Abort(abortReason, true);
            LeaveDeterministicMode();
            GameBridge.RestoreLocalState();
            if (Current == this) Current = null;
            Plugin.Log.LogInfo(abortReason == null ? "Online match finished." : "Online match ended: " + abortReason);
            if (abortReason != null)
            {
                NetplayManager.Instance?.Notify(abortReason);
                GameBridge.BailToMainMenu();
            }
        }

        // ------------------------------------------------------------------ scene switching

        /// <summary>Replaces StageLoader.LoadingStage while a match runs, so all peers switch scenes on the same frame.</summary>
        public void LoadStage(GameData.STAGES stage)
        {
            Menu_Loading.STAGE = stage;
            StageController.STAGE_NUM = stage;
            if (!GameBridge.StageScenes.TryGetValue(stage, out string scene))
            {
                End("Stage " + stage + " cannot be played online.");
                return;
            }
            _loadingSegment = Core.Segment + 1;
            _loadingScene = scene;
            _activationRequested = false;
            Status = "Loading " + GameBridge.StageLabel((int)stage);
            NetplayManager.Instance.StartCoroutine(LoadRoutine(scene, _loadingSegment));
        }

        private IEnumerator LoadRoutine(string scene, int segment)
        {
            _loadOp = SceneManager.LoadSceneAsync(scene);
            _loadOp.allowSceneActivation = false;
            while (_loadOp.progress < 0.9f) yield return null;
            if (_ended || segment != _loadingSegment) yield break;
            Core.LocalLoadReady(segment);
            // Outside battle (VS screen) nothing is gated, so this coroutine also does the activating.
            while (!_ended && !_activationRequested)
            {
                if (!Core.Gating) TryActivate();
                yield return null;
            }
        }

        private void TryActivate()
        {
            if (_loadOp == null || _activationRequested || !Core.ShouldActivate(_loadingSegment)) return;
            _activationRequested = true;
            Core.StopGating();
            _frameOpen = false;
            // Until the new scene is live, the old one idles: no time passes and nobody presses anything.
            for (int i = 0; i < _pads.Length; i++) { _pads[i] = default(PadState); _prevPads[i] = default(PadState); }
            Time.timeScale = 0f;
            _loadOp.allowSceneActivation = true;
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (_ended) return;
            GameBridge.ForgetScene();
            if (scene.name == _loadingScene && _activationRequested)
            {
                int seg = _loadingSegment;
                _loadingScene = null;
                _loadOp = null;
                Core.BeginSegment(seg);
                _frameOpen = false;
                _submittedThisFrame = false;
                for (int i = 0; i < _pads.Length; i++) { _pads[i] = default(PadState); _prevPads[i] = default(PadState); }
                if (Time.timeScale == 0f) Time.timeScale = GameData.GAME_SPEED;
                Random.InitState(Core.Config.Seed ^ (seg * 7919));
                Time.captureDeltaTime = FrameTime;
                Physics.autoSimulation = false;
                FixedStepDriver.Suppress = true;
                NetClock.BeginSegment();
                GameBridge.ForceAlwaysAnimate();
                Status = "Round " + (seg + 1);
            }
            else if (!GameBridge.IsStageScene(scene.name) && Core.Segment >= 0)
            {
                // The game left the battle (results screen / back to character select): the match is over.
                End(null);
            }
        }

        // ------------------------------------------------------------------ per-frame gate

        /// <summary>Runs at the very start of every Update phase, before any game script.</summary>
        public void OnFrame()
        {
            if (_ended) return;
            if (Core.AbortReason != null) { End(Core.AbortReason); return; }

            if (_frameOpen)
            {
                Core.Advance();
                _frameOpen = false;
                _submittedThisFrame = false;
            }

            if (_loadOp != null && Core.Gating) TryActivate();
            if (!Core.Gating) return;

            if (!_submittedThisFrame)
            {
                Core.SubmitLocal(GameBridge.SampleLocal(_localControls));
                _submittedThisFrame = true;
            }

            if (!WaitForInputs()) return;
            PaceFrame();

            var tmp = _prevPads; _prevPads = _pads; _pads = tmp;
            Array.Copy(_scratch, _pads, _pads.Length);
            _frameOpen = true;

            // Same random sequence on every peer each frame, even if some cosmetic code drew numbers on one side only.
            Random.InitState(unchecked(Core.Config.Seed ^ (Core.Segment * 7919) ^ (Core.Frame * 104729)));
            NetClock.Step(Time.deltaTime, FrameTime);

            if (Core.IsCheckFrame)
            {
                Core.RecordLocalState(GameBridge.CaptureState());
                if (Core.Frame % 120 == 0) GameBridge.ForceAlwaysAnimate();
            }
            var fix = Core.TakeCorrection();
            if (fix != null) GameBridge.ApplyCorrection(fix);

            FixedStepDriver.RunOneStep(Time.deltaTime);
            Status = "Round " + (Core.Segment + 1);
        }

        private bool WaitForInputs()
        {
            if (Core.TryGetInputs(_scratch)) { StallMs = 0; return true; }
            var sw = Stopwatch.StartNew();
            var mgr = NetplayManager.Instance;
            long limit = Plugin.StallTimeoutSeconds.Value * 1000L;
            while (sw.ElapsedMilliseconds < limit)
            {
                mgr.WaitForNetwork(2);
                mgr.PumpNetwork();
                if (_ended || Core.AbortReason != null) return false;
                if (Core.TryGetInputs(_scratch))
                {
                    StallMs = sw.Elapsed.TotalMilliseconds;
                    return true;
                }
            }
            var missing = Core.MissingPlayers().Select(p => Core.Config.Names[p]).ToArray();
            End("Lost contact with " + string.Join(", ", missing) + ".");
            return false;
        }

        // ------------------------------------------------------------------ input answers for the game

        /// <summary>Answers a game input query for one of the virtual "N0".."N3" controls.</summary>
        public static bool TryAnswerAxis(string nameWithControl, out float value)
        {
            value = 0f;
            if (!ParseVirtual(nameWithControl, out string action, out int player)) return false;
            var s = Current;
            if (s == null || player >= s._pads.Length) return true;
            var pad = s._pads[player];
            switch (action)
            {
                case "Horizontal": value = PadState.AxisToFloat(pad.H); break;
                case "Vertical": value = PadState.AxisToFloat(pad.V); break;
                case "Crouch": value = PadState.AxisToFloat(pad.CrouchAxis); break;
            }
            return true;
        }

        public static bool TryAnswerButton(string nameWithControl, bool down, bool up, out bool value)
        {
            value = false;
            if (!ParseVirtual(nameWithControl, out string action, out int player)) return false;
            var s = Current;
            if (s == null || player >= s._pads.Length) return true;
            ushort bit = PadState.BitFor(action);
            if (bit == 0) return true; // Start (pause) is disabled online
            bool now = s._pads[player].Has(bit);
            bool before = s._prevPads[player].Has(bit);
            if (down) value = now && !before;
            else if (up) value = !now;
            else value = now;
            return true;
        }

        private static bool ParseVirtual(string name, out string action, out int player)
        {
            action = null; player = -1;
            if (name == null || name.Length < 3) return false;
            char n = name[name.Length - 2], d = name[name.Length - 1];
            if (n != 'N' || d < '0' || d > '3') return false;
            action = name.Substring(0, name.Length - 2);
            player = d - '0';
            return true;
        }
    }
}
