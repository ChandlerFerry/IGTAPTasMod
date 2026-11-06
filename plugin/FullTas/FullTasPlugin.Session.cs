using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using BepInEx;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace IGTAPTasMod
{
    public sealed partial class TasPlugin
    {
        void BuyVmanStart(Movement player)
        {
            globalStats stats = Singleton<globalStats>.Instance;
            SetArea1State(stats, globalStats.area1states.Overgrown, "the Vman start");
            upgradeBox[] boxes = FindObjectsByType<upgradeBox>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (globalStats.globalUpgradeSet upgrade in new[] { globalStats.globalUpgradeSet.vmanTime, globalStats.globalUpgradeSet.VmanStartsWithOmniDash })
            {
                globalStats.globalUpgradeDict[upgrade] = boxes.Where(b => b.upgrade == localUpgrades.localUpgradeSet.GLOBAL && b.globalUpgrade == upgrade)
                    .Sum(b => b.Cap * BoxEffectMult(b));
                _newGameNotes.Add(upgrade + " " + globalStats.globalUpgradeDict[upgrade].ToString(CultureInfo.InvariantCulture));
            }
            upgradeBox start = boxes.FirstOrDefault(b => b.upgrade == localUpgrades.localUpgradeSet.GLOBAL
                && b.globalUpgrade == globalStats.globalUpgradeSet.startVmanSpeedrun && BoxEffectMult(b) == 1.0);
            if (start == null || !start.gameObject.activeInHierarchy || !start.isActive)
            {
                FailNewGame("No active 1x \"Start a VMAN speedrun\" box with Area 1 overgrown.");
                return;
            }
            Bounds box = start.GetComponents<Collider2D>().Select(c => c.bounds).Aggregate((a, b) => { a.Encapsulate(b); return a; });
            RaycastHit2D floor = Physics2D.Raycast(new Vector2(box.center.x, box.min.y - 1f), Vector2.down, 5000f, LayerMask.GetMask("Ground"));
            if (floor.collider == null)
            {
                FailNewGame("No floor under the Vman start box.");
                return;
            }
            float feet = player.transform.position.y - player.GetComponents<BoxCollider2D>().Where(c => c.enabled).Min(c => c.bounds.min.y);
            Rigidbody2D body = player.GetComponent<Rigidbody2D>();
            player.transform.position = new Vector3(box.center.x, floor.point.y + feet + .5f, player.transform.position.z);
            body.position = player.transform.position;
            body.linearVelocity = Vector2.zero;
            Physics2D.SyncTransforms();
            _newGameNotes.Add("player under " + HierarchyPath(start.transform) + " at " + ((Vector2)player.transform.position - CurrentOrigin()).ToString("0.#"));
            _vmanSetupQuiet = _engine.Quiet;
            _engine.Quiet = true;
            _engine.Run(new MacroDefinition { name = "Vman start box", steps = {
                new MacroStep { type = MacroStepType.RawInput, durationTicks = 1, rawJumpPressed = true, rawJumpHeld = true },
                new MacroStep { type = MacroStepType.RawInput, durationTicks = VmanSetupJumpTicks, rawJumpHeld = true } } });
            _vmanSetupPlayer = player;
            _newGamePhase = 4;
            _newGameFrame = Time.frameCount;
        }

        void EndVmanSetupJump()
        {
            _engine.Discard();
            _engine.Quiet = _vmanSetupQuiet;
        }

        void ApplyNewGameRebase(Movement player)
        {
            _newGameRebased = true;
            Vector2 at = _newGameRebaseAt.Value;
            string error = RebaseWorldAround(player, at, out float distance);
            if (error != null) { FailNewGame(error); return; }
            _newGameNotes.Add("rebased around " + at.ToString("0.#") + " (player " + distance.ToString("0.#") + " from it)");
            _newGameSpawn = null;
            _newGamePhase = 2;
            _newGameFrame = Time.frameCount;
        }

        // Replicates FloatingOrigin.MoveOrigin's moves around world point `at`, then a physics sync (exception 6, rebaseAt).
        string RebaseWorldAround(Movement player, Vector2 at, out float distance)
        {
            distance = 0f;
            FloatingOrigin origin = CurrentFloatingOrigin();
            if (origin == null) return "No FloatingOrigin to rebase.";
            var offset = new Vector3(at.x + origin.currentOrigin.x, at.y + origin.currentOrigin.y, 0f);
            Vector2 after = (Vector2)(player.transform.position - offset);
            distance = after.magnitude;
            if (distance >= origin.Threshold)
                return "rebaseAt " + at + " is " + distance.ToString("0.#") + " from the player, past FloatingOrigin's Threshold " + origin.Threshold + ".";
            FloatingOriginMoves[0].Invoke(origin, new object[] { offset });
            Camera.main.GetComponent<cameraMover>().Translate(-offset);
            Camera.main.transform.position += offset;
            foreach (backgroundScroller scroller in FindObjectsByType<backgroundScroller>(FindObjectsSortMode.None)) scroller.Translate(offset);
            if (origin.UpdateParticles) FloatingOriginMoves[1].Invoke(origin, new object[] { offset });
            if (origin.UpdateTrailRenderers) FloatingOriginMoves[2].Invoke(origin, new object[] { offset });
            if (origin.UpdateLineRenderers) FloatingOriginMoves[3].Invoke(origin, new object[] { offset });
            origin.currentOrigin -= offset;
            Physics2D.SyncTransforms();
            return null;
        }

        bool ApplyNewGameTree(Movement player)
        {
            int[] segments = _newGameTree is JArray ? _newGameTree.Select(t => (int)t).ToArray() : null;
            int level = segments == null ? (int)_newGameTree : -1;
            string what = segments == null ? "the tree at level " + level : "the tree with segments " + string.Join(", ", segments);
            TreeController tree = TreeLevel.TheTree();
            if (tree == null) { FailNewGame("No tree for " + what + "."); return false; }
            if (level >= 23) SetArea1State(Singleton<globalStats>.Instance, globalStats.area1states.Overgrown, what);
            if (segments == null) TreeLevel.Apply(tree, level);
            else TreeLevel.Apply(tree, segments);
            SetPath(level >= 23);
            ZoneLoader loader = FindAnyObjectByType<ZoneLoader>();
            int zone = ZoneAt(player.transform.position, loader);
            if (zone != 0 && (zone != loader.activeZone || !ZonesSettled())) LoadZoneNow.Invoke(loader, new object[] { zone });
            _newGameNotes.Add(what + (zone == 0 ? "" : ", zone " + zone + " loaded"));
            return true;
        }

        internal Savestate ReloadSavestate(string id)
        {
            int index = _savestates.FindIndex(s => s.id == id);
            if (index < 0) return null;
            string path = Path.Combine(_savestatesPath, SafeId(id) + ".json");
            if (File.Exists(path)) _savestates[index] = JsonConvert.DeserializeObject<Savestate>(File.ReadAllText(path)) ?? _savestates[index];
            return _savestates[index];
        }

        void ApplyNewGameSavestate(Movement player)
        {
            Savestate state = ReloadSavestate(_newGameSavestate);
            if (state == null) { FailNewGame("No savestate " + _newGameSavestate + "."); return; }
            if (CapabilitiesLocked && state.movementFields.Keys.Any(SavestateService.IsUnlockField))
            {
                FailNewGame("Savestate " + state.name + " sets unlocks: " + CapabilitiesLockedError);
                return;
            }
            SavestateResult restored = SavestateService.Restore(player, state);
            if (!restored.Success) { FailNewGame(restored.Message); return; }
            // The savestate holds no checkpoint, none is written: respawnPoint stays the new game's (FullTasTracker.RestartRefused).
            _newGameSavestateApplied = true;
            _newGameAirStart = state.movementFields.TryGetValue("onGround", out SnapshotValue ground)
                && string.Equals(ground?.value, "false", StringComparison.OrdinalIgnoreCase) ? state : null;
            _newGameNotes.Add("restored savestate " + state.name + (_newGameAirStart != null ? " (in the air: held there until the start)" : ""));
            if (_newGameTree != null && !ApplyNewGameTree(player)) return;
            _newGameSpawn = null;
            _newGamePhase = 2;
            _newGameFrame = Time.frameCount;
        }

        void ParkAtNewGameStart()
        {
            Movement player = FindWorldMovement();
            if (FreshGame && player == _newGamePlayer) TurboRunner.Restore(_newGameStart, player);
        }
    }
}
