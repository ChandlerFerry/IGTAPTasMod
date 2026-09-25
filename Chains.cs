using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using BepInEx;
using HarmonyLib;
using Newtonsoft.Json;
using UnityEngine;

namespace IGTAPTasMod
{
    // Chains: the game played in order after an unlock, one segment after another with no teleport in between. Each
    // segment starts exactly where the previous one ended (position, speed, held jump, timers), so a chain segment's
    // route is solved from that state, not from a quick restart at its course. "restart" between two segments is the
    // game's quick restart (QuickReset: Movement.respawn(true)), the only place a chain goes back.
    public sealed partial class TasPlugin
    {
        public sealed class ChainDef
        {
            // Letters and digits only: route names ("wj/2_WJ 14.86") and offline-best files carry it.
            public string name;
            // The chain's category (none|wj|wjdash|wjdashaj): its unlocks on every tick of every segment.
            public string capabilities;
            // Course ids ("2"), bonuses ("bonus1"), transitions ("<from>to<to>": "1to2", "2tobonus1", "bonus1to2") and
            // "restart" (a quick restart right before the next segment).
            public List<string> segments = new List<string>();
        }

        internal sealed class ChainSegment
        {
            // Among the chain's segments (a restart is not a segment).
            public int index;
            public string token;
            // The game's quick restart is this segment's first act.
            public bool restart;
            // What finishes it: a course's end gate, a bonus's reward box, a transition's end box or next start gate.
            public CourseDef target;
        }

        // A chain being played: in real time from the studio (StartChainPlayback) or in turbo (ReplayChain).
        sealed class ChainRun
        {
            public ChainDef chain;
            public List<ChainSegment> segments;
            // One route per segment played: the chain's first `routes.Count` segments.
            public List<MacroDefinition> routes;
            public int index;
            // The current segment finished; the next one starts in the next tick's Movement.FixedUpdate prefix.
            public bool pendingNext;
            // Physics ticks × fixedDeltaTime of every finished segment.
            public readonly List<float> splits = new List<float>();
            public string failure;
            // Turbo only: stop where segment `captureAt` would start and keep that state as its start (`captured`).
            public int captureAt = -1;
            public TurboRunner.Snapshot captured;
            // Turbo only (audit): the first segment a gate check failed in, and the checks' messages.
            public int violationSegment = -1;
            public List<string> audit;
            public ChainSegment Current { get { return segments[index]; } }
        }

        List<ChainDef> _chains = new List<ChainDef>();
        ChainRun _chainRun, _pendingChain;
        // Set by a chain segment that starts with a quick restart; done in the next Movement.FixedUpdate prefix.
        bool _restartBeforeTick;
        string _lastChainResult;
        static readonly MethodInfo RespawnMethod = AccessTools.Method(typeof(Movement), "respawn");

        string ChainsPath { get { return Path.Combine(Paths.ConfigPath, "IGTAPTasMod", "chains.json"); } }

        // Playback in progress: a route, a chain (also between two of its segments), or one about to start.
        bool PlaybackActive { get { return _engine.IsRunning || _chainRun != null || _pendingChain != null; } }

        void LoadChains()
        {
            try
            {
                if (File.Exists(ChainsPath))
                {
                    _chainsWritten = File.GetLastWriteTimeUtc(ChainsPath);
                    _chains = JsonConvert.DeserializeObject<List<ChainDef>>(File.ReadAllText(ChainsPath)) ?? new List<ChainDef>();
                    return;
                }
            }
            catch (Exception ex) { Logger.LogWarning("chains.json rejected: " + ex.Message); return; }
            // After buying wall jump: Course 1 again, over to Course 2, Course 2, up to Bonus 1, Bonus 1, a quick restart
            // (back to the last checkpoint), then back to Course 2. After buying dash: Course 2, over to Bonus 2, Bonus 2, a
            // quick restart, on to Course 3, Bonus 3 from Course 3's start, a quick restart (Course 3 is timing, so it goes
            // to Course 3's start), then Course 3.
            _chains = new List<ChainDef> {
                new ChainDef { name = "wj", capabilities = "wj",
                    segments = new List<string> { "1", "1to2", "2", "2tobonus1", "bonus1", "restart", "bonus1to2" } },
                new ChainDef { name = "dash", capabilities = "wjdash",
                    segments = new List<string> { "2", "2tobonus2", "bonus2", "restart", "bonus2to3", "bonus3", "restart", "3" } } };
            WriteJsonAtomic(ChainsPath, _chains, false);
            _chainsWritten = File.GetLastWriteTimeUtc(ChainsPath);
        }

        // chains.json edited while the game runs: picked up within a second (Update), never mid-chain.
        DateTime _chainsWritten;
        float _chainsCheckAt;

        void PumpChains()
        {
            if (Time.unscaledTime < _chainsCheckAt || PlaybackActive || _silentEvaluation) return;
            _chainsCheckAt = Time.unscaledTime + 1f;
            if (File.Exists(ChainsPath) && File.GetLastWriteTimeUtc(ChainsPath) != _chainsWritten) LoadChains();
        }

        ChainDef FindChain(string name)
        {
            return _chains.Find(c => string.Equals(c.name, name, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("No chain named \"" + name + "\" in chains.json.");
        }

        static readonly Regex ChainName = new Regex(@"^[A-Za-z0-9]+$");
        static readonly Regex ChainTransition = new Regex(@"^(bonus\d+|\d+)to(bonus\d+|\d+)$", RegexOptions.IgnoreCase);
        // Transitions a chain names that courses.json doesn't have (their start is wherever the chain arrives): ids 1000+.
        readonly Dictionary<string, CourseDef> _chainTransitions = new Dictionary<string, CourseDef>(StringComparer.OrdinalIgnoreCase);

        List<ChainSegment> ChainSegments(ChainDef chain)
        {
            if (!ChainName.IsMatch(chain.name ?? "")) throw new InvalidOperationException("Chain names are letters and digits only (\"" + chain.name + "\").");
            if (!CapabilityLabels.ContainsKey(chain.capabilities ?? ""))
                throw new InvalidOperationException("Chain " + chain.name + ": capabilities must be none, wj, wjdash or wjdashaj.");
            var result = new List<ChainSegment>();
            bool restart = false;
            foreach (string raw in chain.segments ?? new List<string>())
            {
                string token = (raw ?? "").Trim();
                if (token.Equals("restart", StringComparison.OrdinalIgnoreCase))
                {
                    if (result.Count == 0 || restart) throw new InvalidOperationException("Chain " + chain.name + ": a restart follows a segment.");
                    restart = true;
                    continue;
                }
                CourseDef target = ChainTarget(token);
                if (!target.HasCategory(chain.capabilities))
                    throw new InvalidOperationException("Chain " + chain.name + ": " + token + " has no " + CapabilityLabels[chain.capabilities] + " category.");
                if (result.Any(s => string.Equals(s.token, token, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("Chain " + chain.name + ": " + token + " is in it twice.");
                result.Add(new ChainSegment { index = result.Count, token = target.Token, restart = restart, target = target });
                restart = false;
            }
            if (result.Count == 0) throw new InvalidOperationException("Chain " + chain.name + " has no segments.");
            if (restart) throw new InvalidOperationException("Chain " + chain.name + " ends with a restart.");
            // The chain starts like a category route: a quick restart at its first segment's start.
            if (result[0].target.IsTransition) throw new InvalidOperationException("Chain " + chain.name + " starts at a course or a bonus, not a transition.");
            return result;
        }

        CourseDef ChainTarget(string token)
        {
            CourseDef known = _courses.Find(c => string.Equals(c.Token, token, StringComparison.OrdinalIgnoreCase));
            if (known != null) return known;
            if (_chainTransitions.TryGetValue(token, out CourseDef made)) return made;
            Match m = ChainTransition.Match(token);
            if (!m.Success)
                throw new InvalidOperationException("Unknown chain segment \"" + token + "\": a course id, a bonus name, \"<from>to<to>\" or \"restart\".");
            string from = m.Groups[1].Value.ToLowerInvariant(), to = m.Groups[2].Value.ToLowerInvariant();
            var transition = new CourseDef { id = 1000 + _chainTransitions.Count, name = from + "to" + to, from = from, to = to, minCapabilities = "none" };
            if (!to.StartsWith("bonus", StringComparison.Ordinal))
            {
                transition.toCourse = int.Parse(to, CultureInfo.InvariantCulture);
                if (_courses.All(c => c.id != transition.toCourse || c.IsBonus || c.IsTransition))
                    throw new InvalidOperationException("Course " + to + " is not in courses.json.");
            }
            else if (BonusStart(to, out Vector2 end))
            {
                transition.endX = end.x; transition.endY = end.y - 12f;
                transition.endW = transition.endH = BonusArrivalSize;
            }
            else throw new InvalidOperationException("Where " + to + " starts is unknown (courses.json).");
            _chainTransitions[token] = transition;
            return transition;
        }

        // "wj/2tobonus1_WJ": a chain segment's routes are category routes of their own ("wj/2tobonus1_WJ 9.10"), saved only
        // by the gate, which replays them after the chain's routes before them.
        static string ChainCategory(ChainDef chain, ChainSegment segment)
        {
            return chain.name + "/" + segment.token + "_" + CapabilityLabels[chain.capabilities];
        }

        // The segment's fastest gated route; null if it has none.
        MacroDefinition ChainSegmentRoute(ChainDef chain, ChainSegment segment)
        {
            string category = ChainCategory(chain, segment);
            MacroDefinition best = null;
            float bestTime = float.PositiveInfinity;
            foreach (MacroDefinition m in _library.macros)
            {
                Match g = CategoryRoute.Match(m.name ?? "");
                if (!g.Success || !string.Equals(g.Groups[1].Value, category, StringComparison.OrdinalIgnoreCase)) continue;
                float time = float.Parse(g.Groups[2].Value, CultureInfo.InvariantCulture);
                if (time < bestTime) { best = m; bestTime = time; }
            }
            return best;
        }

        // The routes of the chain's first `count` segments; every one must exist.
        List<MacroDefinition> ChainRoutes(ChainDef chain, List<ChainSegment> segments, int count)
        {
            var routes = new List<MacroDefinition>();
            for (int i = 0; i < count; i++)
            {
                MacroDefinition route = ChainSegmentRoute(chain, segments[i]);
                if (route == null)
                    throw new InvalidOperationException("Chain " + chain.name + ": segment " + (i + 1) + " (" + segments[i].token + ") has no route yet; solve it first.");
                routes.Add(route);
            }
            return routes;
        }

        // Studio playback in real time: the chain's first segment from a quick restart at its start (like a category route),
        // then every next segment straight on, through segment `through`; `routeAtThrough` replaces that one's route.
        void StartChainPlayback(ChainDef chain, int through, MacroDefinition routeAtThrough = null)
        {
            if (PlaybackActive || _recorder.IsRecording) throw new InvalidOperationException("Stop playback or recording first.");
            if (_solverRunning) throw new InvalidOperationException("Stop the solver first.");
            List<ChainSegment> segments = ChainSegments(chain);
            if (through < 0 || through >= segments.Count) through = segments.Count - 1;
            List<MacroDefinition> routes = ChainRoutes(chain, segments, through);
            routes.Add(routeAtThrough ?? ChainRoutes(chain, segments, through + 1)[through]);
            EnsureGameUnpaused();
            _pendingChain = new ChainRun { chain = chain, segments = segments, routes = routes };
        }

        // Movement.FixedUpdate prefix: the queued chain starts at its first segment's start.
        void BeginChainPlayback()
        {
            ChainRun run = _pendingChain;
            _pendingChain = null;
            RestoreRouteStart(new MacroDefinition { name = run.segments[0].target.Token + "_" });
            Movement movement = FindWorldMovement();
            if (movement != null)
                foreach (var (field, value) in ApplyAbilities(movement, run.chain.capabilities))
                    if (!_playbackSavedAbilities.ContainsKey(field)) _playbackSavedAbilities[field] = value;
            BeginDeterministicPhysics(movement);
            _brakeTicks = -1;
            _chainRun = run;
            _engine.Run(run.routes[0], _library, true, true, 0, movement == null ? (Vector2?)null : movement.transform.position);
        }

        // Movement.FixedUpdate prefix of the tick after a segment finished: the next segment goes on from here.
        void ContinueChain(Movement movement)
        {
            ChainRun run = _chainRun;
            run.pendingNext = false;
            run.index++;
            if (run.index == run.captureAt)
            {
                // The segment's start state, taken before its quick restart (which every run of it then does first).
                _engine.Continue(new MacroDefinition { name = "Chain segment start", steps = { new MacroStep { type = MacroStepType.RawInput, durationTicks = 1 } } }, true);
                TurboRunner.Snapshot start = TurboRunner.Capture(movement, _solverCourses, _engine.CaptureState(), 0, true);
                // The world's timers fire when this turbo run ends: runs from here start in a settled world, as offline ones do.
                start.pending = Array.FindAll(start.pending, p => p.target == movement);
                run.captured = start;
                _engine.Discard();
                return;
            }
            _restartBeforeTick = run.Current.restart;
            _engine.Continue(run.routes[run.index], true);
        }

        // What the game does on a QuickReset press (Movement.Update): respawn(true), nothing else.
        void QuickRestart(Movement movement)
        {
            RespawnMethod.Invoke(movement, new object[] { true });
        }

        // A chain segment's run ended. True when the chain goes on (its next segment starts on the next physics tick).
        bool ChainSegmentEnded(bool success, string reason)
        {
            ChainRun run = _chainRun;
            if (success && MacroEngine.IsFinishReason(reason))
            {
                run.splits.Add(_engine.AuthoritativeCompletionTime);
                if (run.index + 1 < run.routes.Count || run.index + 1 == run.captureAt)
                {
                    run.pendingNext = true;
                    return true;
                }
            }
            else
            {
                Movement player = FindWorldMovement();
                run.failure = reason + (player == null ? "" : " at " + ((Vector2)player.transform.position).ToString("0.0") + ", input tick " + _engine.RunTicks);
            }
            return false;
        }

        // The end of a studio chain playback (finished, failed or stopped): the save's unlocks back, one result for the chain.
        void FinishChainPlayback(bool success, string reason)
        {
            ChainRun run = _chainRun;
            _chainRun = null;
            _restartBeforeTick = false;
            RestorePlaybackAbilities();
            bool finished = run.failure == null && run.splits.Count == run.routes.Count;
            if (finished && reason == MacroEngine.TransitionFinishReason) _brakeTicks = 0;
            float total = run.splits.Sum();
            string splits = string.Join(" + ", run.splits.Select((t, i) => run.segments[i].token + " " + t.ToString("0.00", CultureInfo.InvariantCulture)));
            _lastChainResult = "Chain " + run.chain.name + (finished ? " finished in " + total.ToString("0.00", CultureInfo.InvariantCulture) + "s"
                : " stopped in segment " + (run.index + 1) + " (" + run.Current.token + "): " + (run.failure ?? reason)) + (splits.Length > 0 ? " [" + splits + "]" : "");
            Logger.LogInfo(_lastChainResult);
            var result = new AttemptResult {
                routeId = "chain:" + run.chain.name,
                routeName = "Chain " + run.chain.name + " through " + run.routes.Count + " of " + run.segments.Count,
                success = finished,
                completionTicks = Mathf.RoundToInt(total / Time.fixedDeltaTime),
                completionTime = total,
                timingSource = "Physics ticks per segment: " + splits,
                deterministicPhysics = _deterministicBody != null,
                failureAction = finished ? -1 : run.index,
                failureReason = finished ? null : run.Current.token + ": " + (run.failure ?? reason) };
            _results.Insert(0, result);
            WriteJsonAtomic(Path.Combine(_resultsPath, result.timestampUtc.Replace(":", "-") + "-" + result.id + ".json"), result, false);
            EndDeterministicPhysics();
        }

        // Stops a route or chain playback, including a chain between two of its segments.
        void StopPlayback(string reason)
        {
            _pendingReplay = null;
            _pendingReplayStartDelayTicks = 0;
            _pendingChain = null;
            if (_engine.IsRunning) _engine.Abort(reason);
            if (_chainRun != null && !_silentEvaluation) FinishChainPlayback(false, reason);
        }

        // Turbo: the chain's `routes` played from its start state (_chainStartState), each segment straight after the one
        // before, exactly as studio playback does. With `captureAt` it stops where that segment would start and keeps the
        // state (ChainRun.captured); with `audit` every tick goes through the gate's checks (AuditTick).
        ChainRun ReplayChain(ChainDef chain, List<ChainSegment> segments, List<MacroDefinition> routes, int captureAt, bool audit)
        {
            Movement player = FindWorldMovement();
            TurboRunner.Snapshot from = _chainStartState;
            var run = new ChainRun { chain = chain, segments = segments, routes = routes, captureAt = captureAt, audit = audit ? new List<string>() : null };
            List<(string field, object value)> savedAbilities = ApplyAbilities(player, chain.capabilities);
            string savedCaps = _solverCaps;
            _solverCaps = chain.capabilities;
            _audit = run.audit;
            _silentEvaluation = true;
            _engine.Quiet = true;
            _chainRun = run;
            int maxTicks = routes.Sum(r => r.steps.Sum(s => Math.Max(1, s.durationTicks))) + 300 * (routes.Count + 1);
            try
            {
                TurboRunner.Restore(from, player);
                BeginDeterministicPhysics(player);
                _engine.Resume(from.engine, routes[0].steps, 0);
                TurboRunner.Run(player, () => (_engine.IsRunning || run.pendingNext) && run.captured == null, maxTicks, () =>
                {
                    _engine.Clock++;
                    if (run.audit == null || !_engine.IsRunning) return;
                    int before = run.audit.Count;
                    AuditTick(player, (int)_engine.RunTicks);
                    if (run.audit.Count > before && run.violationSegment < 0) run.violationSegment = run.index;
                }, from);
                if (_engine.IsRunning) _engine.Abort("Chain replay ended");
            }
            finally
            {
                _chainRun = null;
                _restartBeforeTick = false;
                _audit = null;
                _silentEvaluation = false;
                _engine.Quiet = false;
                _solverCaps = savedCaps;
                foreach (var (field, value) in savedAbilities) SetMovementField(player, field, value);
            }
            return run;
        }

        static string ChainFailure(ChainRun run)
        {
            int at = run.splits.Count;
            return "Chain " + run.chain.name + ": segment " + (at + 1) + " (" + run.segments[at].token + ", " + run.routes[at].name + ") "
                + (run.failure ?? "did not finish") + " from where the segments before it end; re-solve it.";
        }

        // The chain's start: _startState of its first segment (a quick restart there), shared by every segment's capture.
        TurboRunner.Snapshot _chainStartState;
        string _chainStartKey;
        Vector2 _chainStartOrigin;

        // _startState for the solver's chain segment: the chain's routes before it, replayed in turbo from the chain's start.
        void CaptureChainSegmentStart(bool fresh)
        {
            string key = _solverChain.name + " " + _solverChain.capabilities + " " + _solverPark.Token;
            if (fresh || _chainStartState == null || _chainStartKey != key || CurrentOrigin() != _chainStartOrigin)
            {
                CourseDef target = _solverTarget;
                bool restart = _solverRestart;
                _solverTarget = _solverPark;
                _solverRestart = false;
                _startState = null;
                try { Evaluate(new List<SolverTick> { new SolverTick() }, out _, out _, out _); }
                finally { _solverTarget = target; _solverRestart = restart; }
                if (_startState == null) throw new InvalidOperationException("The chain's start state was not captured.");
                _chainStartState = _startState;
                _chainStartKey = key;
                _chainStartOrigin = CurrentOrigin();
            }
            _startState = null;
            if (_solverSegment == 0)
            {
                _startState = _chainStartState;
                _startOrigin = _chainStartOrigin;
                return;
            }
            ChainRun run = ReplayChain(_solverChain, _solverSegments, ChainRoutes(_solverChain, _solverSegments, _solverSegment), _solverSegment, false);
            if (run.captured == null) throw new InvalidOperationException(ChainFailure(run));
            _startState = run.captured;
            _startOrigin = CurrentOrigin();
        }

        // A faster segment changes where every later one starts. Each later route is replayed from its new start through
        // the gate's checks: one that still finishes is renamed to its new time; the first that doesn't, and every one
        // after it, gets " stale" (no longer the chain's route; kept as a seed for re-solving that segment).
        void RevalidateChainTail()
        {
            ChainDef chain = _solverChain;
            List<ChainSegment> segments = _solverSegments;
            var routes = new List<MacroDefinition>();
            foreach (ChainSegment s in segments)
            {
                MacroDefinition route = ChainSegmentRoute(chain, s);
                if (route == null) break;
                routes.Add(route);
            }
            if (routes.Count <= _solverSegment + 1) return;
            ChainRun run = ReplayChain(chain, segments, routes, -1, true);
            int valid = run.violationSegment >= 0 ? Math.Min(run.violationSegment, run.splits.Count) : run.splits.Count;
            for (int j = _solverSegment + 1; j < routes.Count; j++)
            {
                string category = ChainCategory(chain, segments[j]);
                string old = routes[j].name;
                routes[j].name = j < valid ? category + " " + run.splits[j].ToString("0.00", CultureInfo.InvariantCulture) : old + " stale";
                string line = "Chain " + chain.name + ": " + old + (j < valid ? " still finishes after the new " + segments[_solverSegment].token + ", now " + routes[j].name
                    : " no longer holds up after the new " + segments[_solverSegment].token + " (" + (j == valid && run.audit.Count > 0 && run.violationSegment == j
                        ? run.audit[0] : run.failure ?? "no finish") + "): marked stale");
                _solverLog.Add(line);
                Logger.LogInfo(line);
            }
        }

        // The studio's view of every chain: its segments with their routes and times.
        object[] BuildChainWebState()
        {
            return _chains.Select(chain =>
            {
                List<ChainSegment> segments;
                try { segments = ChainSegments(chain); }
                catch (Exception ex) { return (object)new { name = chain.name, capabilities = chain.capabilities, error = ex.Message, segments = new object[0] }; }
                var rows = segments.Select(s =>
                {
                    MacroDefinition route = ChainSegmentRoute(chain, s);
                    Match g = route == null ? null : CategoryRoute.Match(route.name);
                    string prefix = ChainCategory(chain, s) + " ";
                    return new { index = s.index, token = s.token, restart = s.restart, bonus = s.target.IsBonus, @from = s.target.from, to = s.target.to,
                        routeId = route?.id, time = g == null ? (float?)null : float.Parse(g.Groups[2].Value, CultureInfo.InvariantCulture),
                        staleRoutes = _library.macros.Count(m => m.name != null && m.name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && m.name.EndsWith(" stale", StringComparison.Ordinal)) };
                }).ToArray();
                return new { name = chain.name, capabilities = chain.capabilities, error = (string)null, segments = rows,
                    total = rows.All(r => r.time != null) ? rows.Sum(r => r.time.Value) : (float?)null };
            }).ToArray();
        }

        object BuildChainRunWebState()
        {
            ChainRun run = _silentEvaluation ? null : _chainRun ?? _pendingChain;
            return new { running = run == null ? null : new { chain = run.chain.name, segment = run.index, of = run.routes.Count, token = run.Current.token,
                splits = run.splits.ToArray() }, last = _lastChainResult };
        }
    }
}
