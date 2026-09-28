using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.SceneManagement;

namespace IGTAPTasMod
{
    public sealed partial class TasPlugin
    {
        private static object GetMovementField(Movement movement, string name)
        {
            try
            {
                if (name == BlueActiveField) return BlockSwapperField?.GetValue(movement) is colouredBlockSwapper swapper && swapper != null ? swapper.isBlueActive : (object)null;
                FieldInfo f = AccessTools.Field(typeof(Movement), name);
                return f == null ? null : f.GetValue(movement);
            }
            catch { return null; }
        }
        bool CapabilitiesLocked { get { return _settings.lockCapabilities; } }
        internal static bool UnlocksLocked { get { return Instance != null && Instance.CapabilitiesLocked; } }

        private static void SetMovementField(Movement movement, string name, object value)
        {
            if (Instance != null && Instance.CapabilitiesLocked && (name.EndsWith("Unlocked") || name == "maxAirDashes" || name == "airDashesLeft" || name == "maxAirJumps" || name == "airJumpsLeft")) return;
            try
            {
                if (name == BlueActiveField)
                {
                    if (value is bool blue && BlockSwapperField?.GetValue(movement) is colouredBlockSwapper swapper && swapper != null) swapper.swapBlocks(blue);
                    return;
                }
                FieldInfo f = AccessTools.Field(typeof(Movement), name);
                if (f != null) f.SetValue(movement, value);
            }
            catch { }
        }

        private static bool GetMovementBool(Movement movement, string name, bool fallback)
        {
            object v = GetMovementField(movement, name);
            return v is bool b ? b : fallback;
        }

        private void DisableSharedAutosave()
        {
            try
            {
                object saveloader = FindAnyObjectByType<Saveloader>();
                if (saveloader == null) { _bootstrapNotes.Add("no Saveloader found"); return; }
                FieldInfo isSaving = AccessTools.Field(saveloader.GetType(), "IsSaving");
                if (isSaving != null)
                {
                    isSaving.SetValue(saveloader, false);
                    _bootstrapNotes.Add("disabled Saveloader autosave (shared Savedata must stay untouched)");
                }
                MonoBehaviour mb = saveloader as MonoBehaviour;
                if (mb != null) mb.CancelInvoke();
            }
            catch (Exception ex) { _bootstrapNotes.Add("autosave disable failed: " + ex.Message); }
        }

        private void PumpBootstrap()
        {
            if (_bootstrapRequest == null || _bootstrapPhase == 0) return;
            try
            {
                if (_bootstrapPhase == 1)
                {
                    _bootstrapPhase = 2;
                    return;
                }
                if (_bootstrapPhase == 2)
                {
                    DisableSharedAutosave();
                    string sceneName = SceneManager.GetActiveScene().name;
                    _bootstrapNotes.Add("active scene: " + sceneName);
                    Movement[] all = FindObjectsByType<Movement>(FindObjectsInactive.Include, FindObjectsSortMode.None);
                    if (all == null || all.Length == 0)
                    {
                        FailBootstrap("No Movement instance after scene load.");
                        return;
                    }
                    var inventory = new List<object>();
                    int menuCount = 0;
                    Movement world = null;
                    foreach (Movement m in all)
                    {
                        if (m == null) continue;
                        bool onMenu = GetMovementBool(m, "isOnMainMenu", false);
                        if (onMenu) menuCount++;
                        inventory.Add(new
                        {
                            name = m.name,
                            active = m.gameObject.activeInHierarchy,
                            isOnMainMenu = onMenu,
                            hasCam = GetMovementField(m, "cam") != null,
                            hasCollider = GetMovementField(m, "normalCollider") != null,
                            x = m.transform.position.x - CurrentOrigin().x,
                            y = m.transform.position.y - CurrentOrigin().y
                        });
                        if (!onMenu && world == null) world = m;
                    }
                    if (world == null) world = FindWorldMovement();
                    if (world == null) { FailBootstrap("No usable Movement after scene load.", inventory); return; }

                    SetMovementField(world, "isOnMainMenu", false);
                    if (world.transform.parent != null) world.transform.parent.gameObject.SetActive(true);
                    world.gameObject.SetActive(true);

                    if (GetMovementField(world, "cam") == null)
                    {
                        try
                        {
                            object camMover = FindAnyObjectByType<cameraMover>();
                            if (camMover != null) { SetMovementField(world, "cam", camMover); _bootstrapNotes.Add("assigned cam from scene cameraMover"); }
                            else _bootstrapNotes.Add("still no cameraMover in scene");
                        }
                        catch (Exception ex) { _bootstrapNotes.Add("cam lookup failed: " + ex.Message); }
                    }

                    string folder = _bootstrapBody != null ? Text(_bootstrapBody, "folder") : null;
                    if (string.IsNullOrEmpty(folder)) folder = "/Savedata";
                    try
                    {
                        MethodInfo loadMethod = AccessTools.Method(typeof(Movement), "load");
                        if (loadMethod != null) { loadMethod.Invoke(world, new object[] { folder }); _bootstrapNotes.Add("loaded " + folder); }
                    }
                    catch (Exception ex) { _bootstrapNotes.Add("load failed: " + ex.Message); }

                    float rx = _bootstrapBody != null && _bootstrapBody["respawnPointX"] != null ? (float)_bootstrapBody["respawnPointX"] : 4783.43359f;
                    float ry = _bootstrapBody != null && _bootstrapBody["respawnPointY"] != null ? (float)_bootstrapBody["respawnPointY"] : 1878.2251f;
                    SetMovementField(world, "respawnPoint", new Vector2(rx, ry));
                    SetMovementField(world, "courseResetPoint", Vector2.zero);
                    SetMovementField(world, "isRespawningAtCheckpoints", CheckpointRespawns);
                    SetMovementField(world, "init", false);

                    try
                    {
                        object zoneLoader = FindAnyObjectByType<ZoneLoader>();
                        if (zoneLoader == null) zoneLoader = AccessTools.TypeByName("ZoneLoader") != null
                            ? typeof(ZoneLoader).GetField("Instance", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)?.GetValue(null)
                            : null;
                        if (zoneLoader != null)
                        {
                            MethodInfo loadZone = AccessTools.Method(zoneLoader.GetType(), "LoadZone", new[] { typeof(int), typeof(bool) });
                            if (loadZone != null)
                            {
                                loadZone.Invoke(zoneLoader, new object[] { 1, true });
                                _bootstrapNotes.Add("ZoneLoader.LoadZone(1, force)");
                            }
                        }
                        else _bootstrapNotes.Add("no ZoneLoader instance");
                    }
                    catch (Exception ex) { _bootstrapNotes.Add("LoadZone failed: " + ex.Message); }

                    EnsureGameUnpaused();

                    bool respawnRan = false;
                    try
                    {
                        MethodInfo respawnMethod = AccessTools.Method(typeof(Movement), "respawn");
                        if (respawnMethod != null) { respawnMethod.Invoke(world, new object[] { true }); respawnRan = true; _bootstrapNotes.Add("respawn(true) completed"); }
                    }
                    catch (Exception ex)
                    {
                        _bootstrapNotes.Add("respawn(true) threw: " + (ex.InnerException != null ? ex.InnerException.Message : ex.Message));
                    }
                    if (!respawnRan) { ApplyRestartGeometry(world); _bootstrapNotes.Add("applied respawn geometry manually"); }

                    Vector2 pos = world.transform.position;
                    Rigidbody2D body2d = world.GetComponent<Rigidbody2D>();
                    if (body2d != null) { body2d.position = pos; body2d.linearVelocity = Vector2.zero; }
                    Physics2D.SyncTransforms();

                    int colliderCount = FindObjectsByType<Collider2D>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length;
                    Vector2 respawnPoint = GetMovementField(world, "respawnPoint") is Vector2 rpv ? rpv : Vector2.zero;
                    Vector2 courseResetPoint = GetMovementField(world, "courseResetPoint") is Vector2 cr ? cr : Vector2.zero;
                    FinishBootstrap(new
                    {
                        ok = true,
                        scene = sceneName,
                        movementCount = all.Length,
                        menuCount = menuCount,
                        movements = inventory,
                        colliderCount = colliderCount,
                        notes = _bootstrapNotes.ToArray(),
                        position = new { x = pos.x, y = pos.y },
                        expectedOrdinaryPose = new { x = 4783.434, y = 1866.225 },
                        poseMatches = Math.Abs(pos.x - 4783.434f) < 0.02 && Math.Abs(pos.y - 1866.225f) < 0.02,
                        respawnPoint = new { x = respawnPoint.x, y = respawnPoint.y },
                        courseResetPoint = new { x = courseResetPoint.x, y = courseResetPoint.y },
                        onGround = GetMovementBool(world, "onGround", false),
                        facingRight = world.facingRight,
                        wallJumpUnlocked = GetMovementBool(world, "wallJumpUnlocked", false),
                        dashUnlocked = GetMovementBool(world, "dashUnlocked", false),
                        isDead = GetMovementBool(world, "isDead", false)
                    });
                    return;
                }
            }
            catch (Exception ex)
            {
                FailBootstrap(ex.ToString());
            }
        }

        private void FinishBootstrap(object payload)
        {
            WebStudioRequest req = _bootstrapRequest;
            _bootstrapRequest = null;
            _bootstrapPhase = 0;
            _bootstrapBody = null;
            _bootstrapNotes.Clear();
            if (req == null) return;
            req.Response = JsonConvert.SerializeObject(payload);
            req.Completed.Set();
        }

        private void FailBootstrap(string error, object extra = null)
        {
            WebStudioRequest req = _bootstrapRequest;
            _bootstrapRequest = null;
            _bootstrapPhase = 0;
            _bootstrapBody = null;
            var notes = new List<string>(_bootstrapNotes);
            _bootstrapNotes.Clear();
            if (req == null) return;
            req.StatusCode = 500;
            req.Response = JsonConvert.SerializeObject(new { ok = false, error = error, notes = notes, extra = extra });
            req.Completed.Set();
        }

        private object BootstrapOracle(WebStudioRequest request, JObject body)
        {
            if (_bootstrapRequest != null || _newGamePhase != 0)
            {
                request.StatusCode = 409;
                return new { ok = false, error = _newGamePhase != 0 ? "A new game is loading." : "A bootstrap is already in progress." };
            }
            _newGameSession = false;
            _newGameStart = null;
            _newGamePlayer = null;
            _bootstrapNotes.Clear();
            string sceneName = SceneManager.GetActiveScene().name;
            bool hasCamera = FindAnyObjectByType<cameraMover>() != null;
            _bootstrapNotes.Add("requested from scene: " + sceneName);

            if (!hasCamera)
            {
                _bootstrapNotes.Add("no cameraMover; loading Overworld scene");
                try { SceneManager.LoadScene("Overworld"); }
                catch (Exception ex)
                {
                    request.StatusCode = 500;
                    return new { ok = false, error = "LoadScene(Overworld) failed: " + ex, notes = _bootstrapNotes };
                }
                _bootstrapRequest = request;
                _bootstrapBody = body;
                request.AsyncHandling = true;
                _bootstrapPhase = 1;
                return null;
            }

            _bootstrapRequest = request;
            _bootstrapBody = body;
            request.AsyncHandling = true;
            _bootstrapPhase = 2;
            return null;
        }

        void StartNewGame(WebStudioRequest request, bool vman, string savestate, Vector2? rebaseAt = null)
        {
            if (PlaybackActive || _recorder.IsRecording) throw new InvalidOperationException("Stop playback or recording first.");
            if (_solverRunning) throw new InvalidOperationException("Stop the solver first.");
            LoadNewGame(request, vman, savestate, rebaseAt);
        }

        void LoadNewGame(WebStudioRequest request, bool vman, string savestate, Vector2? rebaseAt = null)
        {
            if (_newGamePhase != 0) throw new InvalidOperationException("A new game is already loading.");
            if (_bootstrapRequest != null) throw new InvalidOperationException("A bootstrap is in progress.");
            if (savestate != null && _savestates.Find(s => s.id == savestate) == null) throw new InvalidOperationException("No savestate " + savestate + ".");
            _newGameSession = true;
            globalStats.difficultyLevel = 0;
            Time.timeScale = 1f;
            VmanScript.isCurrentlyVman = false;
            VmanScript.metaVman = false;
            VmanScript.vmanScore = 0.0;
            VmanScript.vmanTimeLeft = 0.0;
            VmanScript.VmanStatingUpgrades = null;
            SceneManager.LoadScene("Overworld");
            _newGameStart = null;
            _newGamePlayer = null;
            _newGameVman = vman;
            _newGameSavestate = savestate;
            _newGameSavestateApplied = false;
            _newGameAirStart = null;
            _newGameRebaseAt = savestate == null ? null : rebaseAt;
            _newGameRebased = false;
            _newGameUnlocks = default;
            _vmanSetupPlayer = null;
            ClearEngineSimStarts();
            _newGameNotes.Clear();
            _newGameFrame = Time.frameCount;
            _newGamePhase = 1;
            _newGameRequest = request;
            if (request != null) request.AsyncHandling = true;
        }

        void PumpNewGame()
        {
            if (_newGamePhase != 1 && _newGamePhase != 2 && _newGamePhase != 3 && _newGamePhase != 4 && _newGamePhase != 5) return;
            int frames = Time.frameCount - _newGameFrame;
            if (_newGamePhase == 4)
            {
                if (VmanScript.isCurrentlyVman)
                {
                    _newGameNotes.Add("the game bought the Vman start box");
                    EndVmanSetupJump();
                    _newGamePhase = 5;
                    _newGameFrame = Time.frameCount;
                }
                else if (frames > 300) { EndVmanSetupJump(); FailNewGame("The player under the Vman start box did not buy it within 300 frames."); }
                return;
            }
            if (_newGamePhase == 5)
            {
                Movement next = FindWorldMovement();
                if (next != null && next != _vmanSetupPlayer && SceneManager.GetActiveScene().name == "Overworld")
                {
                    _newGameNotes.Add("the Vman world loaded");
                    _vmanSetupPlayer = null;
                    _newGamePhase = 2;
                    _newGameFrame = Time.frameCount;
                }
                else if (frames > 900) FailNewGame("The Vman world did not load within 900 frames of the start box's purchase.");
                return;
            }
            if (frames > 600)
            {
                FailNewGame(_newGamePhase == 3 ? "The new game's player did not come to rest on the ground within 600 frames." : "The new game's player did not settle within 600 frames.");
                return;
            }
            if (_newGamePhase == 3) return;
            if (_newGamePhase == 1)
            {
                if (frames < 2 || SceneManager.GetActiveScene().name != "Overworld") return;
                _newGameNotes.Add("scene Overworld loaded");
                _newGamePhase = 2;
                _newGameFrame = Time.frameCount;
                return;
            }
            Movement player = FindWorldMovement();
            if (player == null || IsMenuPlayer(player)) return;
            if (!player.gameObject.activeInHierarchy)
            {
                if (frames < 5) return;
                if (player.transform.parent != null) player.transform.parent.gameObject.SetActive(true);
                player.gameObject.SetActive(true);
                _newGameNotes.Add("activated the world player");
                return;
            }
            if (frames < 10 || !ZonesSettled()) return;
            EnsureGameUnpaused();
            _newGameRestTicks = 0;
            _newGameFrame = Time.frameCount;
            _newGamePhase = 3;
        }

        /// <summary>A zip mover's reset (cleared by a Unity-run coroutine two FixedUpdates on): a start taken then holds it half done.</summary>
        static bool MoverResetting()
        {
            foreach (PlatformMover m in FindObjectsByType<PlatformMover>(FindObjectsSortMode.None)) if (MoverReset(m)) return true;
            return false;
        }

        bool NewGamePlayerAtRest(Movement player)
        {
            if (_newGameAirStart != null)
            {
                // Held in the air where the savestate put it (gravity moved it last tick), then counted as a rest.
                SavestateResult held = SavestateService.Restore(player, _newGameAirStart, MacroLibrary.CurrentSchemaVersion);
                if (!held.Success) { FailNewGame(held.Message); return false; }
                Physics2D.SyncTransforms();
                return ++_newGameRestTicks > NewGameRestTicks;
            }
            bool still = player.onGround && player.Velocity.x == 0f && player.cutsceneMode == Movement.cutsceneModes.none;
            _newGameRestTicks = still ? _newGameRestTicks + 1 : 0;
            return _newGameRestTicks > NewGameRestTicks;
        }

        void CaptureNewGameStart(Movement player)
        {
            if (_newGameVman && !VmanScript.isCurrentlyVman) { BuyVmanStart(player); return; }
            if (_newGameSavestate != null && !_newGameSavestateApplied) { ApplyNewGameSavestate(player); return; }
            if (_newGameRebaseAt.HasValue && !_newGameRebased) { ApplyNewGameRebase(player); return; }
            _newGamePhase = 0;
            courseScript[] courses = FindObjectsByType<courseScript>(FindObjectsSortMode.None);
            bool quiet = _engine.Quiet;
            _engine.Quiet = true;
            _engine.Run(new MacroDefinition { name = "New game start", steps = { new MacroStep { type = MacroStepType.RawInput, durationTicks = 1 } } },
                false, true);
            UnityEngine.Random.InitState(FullTasSeed);
            _newGameStart = TurboRunner.Capture(player, courses, _engine.CaptureState(), 0, true);
            _engine.Discard();
            _engine.Quiet = quiet;
            _newGamePlayer = player;
            _newGameUnlocks = _newGameSavestate == null ? default : FullTasUnlocks.Of(player);
            _newGameOrigin = CurrentOrigin();
            if (_fullTasPlay != null) _fullTasPlay.waitingForNewGame = false;
            Vector2 position = (Vector2)player.transform.position - CurrentOrigin();
            Vector2 respawn = GetMovementField(player, "respawnPoint") is Vector2 r ? r : Vector2.zero;
            double cash = globalStats.currencyLookup != null && globalStats.currencyLookup.TryGetValue(globalStats.Currencies.Cash, out double c) ? c : 0.0;
            Logger.LogInfo("New game: player at " + position.ToString("0.###") + ", respawnPoint " + respawn.ToString("0.###") + ", Cash " + cash);
            WebStudioRequest request = _newGameRequest;
            _newGameRequest = null;
            if (request == null) return;
            request.Response = JsonConvert.SerializeObject(new {
                ok = true, position = new { x = position.x, y = position.y }, respawnPoint = new { x = respawn.x, y = respawn.y }, cash,
                unlocks = new { wallJump = GetMovementBool(player, "wallJumpUnlocked", false), dash = GetMovementBool(player, "dashUnlocked", false),
                    maxAirDashes = GetMovementField(player, "maxAirDashes"), doubleJump = GetMovementBool(player, "doubleJumpUnlocked", false),
                    maxAirJumps = GetMovementField(player, "maxAirJumps"), omniDash = GetMovementBool(player, "omniDashUnlocked", false) },
                vman = !VmanScript.isCurrentlyVman ? null : new { timeLeft = VmanScript.vmanTimeLeft,
                    omniDashBox = globalStats.globalUpgradeDict[globalStats.globalUpgradeSet.VmanStartsWithOmniDash] },
                notes = _newGameNotes.ToArray() });
            request.Completed.Set();
        }

        void FailNewGame(string error)
        {
            _newGamePhase = 0;
            Logger.LogWarning("New game: " + error);
            DropPendingFullTasPlayback("the new game did not load: " + error);
            WebStudioRequest request = _newGameRequest;
            _newGameRequest = null;
            if (request == null) return;
            request.StatusCode = 500;
            request.Response = JsonConvert.SerializeObject(new { ok = false, error, notes = _newGameNotes.ToArray() });
            request.Completed.Set();
        }

        bool NewGameIs(FullTasRunDef run) { return _newGameVman == run.Vman && _newGameSavestate == run.savestate && _newGameRebaseAt == run.RebaseAt; }

        TurboRunner.Snapshot NewGameStart(FullTasRunDef run)
        {
            if (!FreshGameInWorld || !NewGameIs(run))
                throw new InvalidOperationException("A run from " + (run.Vman ? "a Vman speedrun" : "a new game") + (run.savestate == null ? "" : " and savestate " + run.savestate)
                    + " needs a fresh one: POST /api/session/newgame {" + (run.Vman ? "\"vman\": true" : "") + (run.Vman && run.savestate != null ? ", " : "")
                    + (run.savestate == null ? "" : "\"savestate\": \"" + run.savestate + "\"") + "} first.");
            return _newGameStart;
        }
        internal static int OmniLevel { get { return CapsLadder.Length - 1; } }
        internal static int TopLevel { get { return OmniLevel - 1; } }

        internal static int CapsLevel(string caps)
        {
            return Math.Max(0, Array.IndexOf(CapsLadder, caps));
        }

        internal static string CapsFor(string label)
        {
            return label.Replace("_", "").ToLowerInvariant();
        }

        internal static IEnumerable<(string field, object value)> CategoryAbilities(string caps)
        {
            int level = CapsLevel(caps);
            int dashes = level >= 2 ? 1 : 0;
            int jumps = level >= 3 ? 1 : 0;
            var abilities = new List<(string, object)> { ("wallJumpUnlocked", level >= 1), ("dashUnlocked", level >= 2),
                ("maxAirDashes", dashes), ("airDashesLeft", dashes),
                ("doubleJumpUnlocked", level >= 3), ("maxAirJumps", jumps), ("airJumpsLeft", jumps),
                ("blockSwapUnlocked", level >= 4), ("omniDashUnlocked", level >= 5) };
            // Below block swap the blocks start unswapped, as a new game has them (Movement.Start: blue active); with it the
            // game's own respawn sets them (isBlueActiveOnRespawn).
            if (level < 4) abilities.Add((BlueActiveField, true));
            return abilities;
        }

        internal static List<(string field, object value)> ApplyAbilities(Movement movement, string caps)
        {
            var saved = new List<(string, object)>();
            foreach (var (field, value) in CategoryAbilities(caps))
            {
                saved.Add((field, GetMovementField(movement, field)));
                SetMovementField(movement, field, value);
            }
            return saved;
        }

        private void ApplyCategoryAbilities(MacroDefinition route, Movement movement)
        {
            Match category = Regex.Match(route.name ?? "", @"^([^_\s]+)_(NONE|WJ_DS_DJ_BS_OM|WJ_DS_DJ_BS|WJ_DS_DJ|WJ_DS|WJ)(?: |$)");
            if (!category.Success || movement == null) return;
            string caps = CapsFor(category.Groups[2].Value);
            foreach (var (field, value) in ApplyAbilities(movement, caps))
                if (!_playbackSavedAbilities.ContainsKey(field)) _playbackSavedAbilities[field] = value;
        }

        private int SaveUnlockLevel(Movement movement)
        {
            Func<string, object> saved = field => _playbackSavedAbilities.TryGetValue(field, out object value) ? value : GetMovementField(movement, field);
            Func<string, bool> on = field => saved(field) is bool b && b;
            Func<string, bool> some = field => saved(field) is int n && n > 0;
            if (!on("wallJumpUnlocked")) return 0;
            if (!on("dashUnlocked") || !some("maxAirDashes")) return 1;
            if (!on("doubleJumpUnlocked") || !some("maxAirJumps")) return 2;
            if (!on("blockSwapUnlocked")) return 3;
            if (!on("omniDashUnlocked")) return 4;
            return 5;
        }

        private void RestorePlaybackAbilities()
        {
            Movement movement = FindWorldMovement();
            if (movement != null)
                foreach (KeyValuePair<string, object> ability in _playbackSavedAbilities) SetMovementField(movement, ability.Key, ability.Value);
            _playbackSavedAbilities.Clear();
        }
    }
}
