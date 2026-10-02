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
        private void LoadLibrary()
        {
            if (!File.Exists(_libraryPath))
            {
                _library = new MacroLibrary();
                SaveLibrary();
                return;
            }
            try
            {
                MacroLibrary loaded = JsonConvert.DeserializeObject<MacroLibrary>(File.ReadAllText(_libraryPath));
                ValidateLibrary(loaded);
                _library = loaded;
                int selected = string.IsNullOrEmpty(_settings.selectedRouteId) ? -1 : _library.macros.FindIndex(r => r.id == _settings.selectedRouteId);
                if (selected >= 0) _library.selectedIndex = selected;
                bool recovered = RecoverTasJournal();
                if (NormalizeLibraryFrameTiming(_library) | RetireLapFarms() | PruneSlowerRoutes() | recovered) SaveLibrary();
                if (recovered) DeleteTasJournal();
            }
            catch (Exception ex)
            {
                Logger.LogError("Routes file rejected; starting with the current schema: " + ex.Message);
                Quarantine(_libraryPath);
                _library = new MacroLibrary();
                SaveLibrary();
            }
        }

        private void LoadSettings()
        {
            try
            {
                if (File.Exists(_settingsPath))
                {
                    StudioSettings loaded = JsonConvert.DeserializeObject<StudioSettings>(File.ReadAllText(_settingsPath));
                    if (loaded == null || loaded.schemaVersion != StudioSettings.CurrentSchemaVersion)
                        throw new InvalidDataException("Unsupported settings schema.");
                    _settings = loaded;
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning("Settings rejected: " + ex.Message);
                Quarantine(_settingsPath);
                _settings = new StudioSettings();
            }
        }

        private void SaveSettings()
        {
            WriteJsonAtomic(_settingsPath, _settings, true);
        }

        private void LoadRecordings()
        {
            try
            {
                Directory.CreateDirectory(_recordingsPath);
                foreach (string path in Directory.GetFiles(_recordingsPath, "*.json"))
                {
                    try
                    {
                        RecordingDefinition recording = JsonConvert.DeserializeObject<RecordingDefinition>(File.ReadAllText(path));
                        ValidateRecording(recording);
                        InputRecorder.Reprocess(recording);
                        _recordings.Add(recording);
                    }
                    catch (Exception ex)
                    {
                        Logger.LogWarning("Recording rejected " + Path.GetFileName(path) + ": " + ex.Message);
                        Quarantine(path);
                    }
                }
                _recordings.Sort((a, b) => string.CompareOrdinal(b.createdUtc, a.createdUtc));
            }
            catch (Exception ex) { Logger.LogError("Could not load recordings: " + ex); }
        }

        private void LoadSavestates()
        {
            try
            {
                Directory.CreateDirectory(_savestatesPath);
                foreach (string path in Directory.GetFiles(_savestatesPath, "*.json"))
                {
                    try
                    {
                        Savestate savestate = JsonConvert.DeserializeObject<Savestate>(File.ReadAllText(path));
                        if (savestate == null || savestate.schemaVersion != Savestate.CurrentSchemaVersion || string.IsNullOrEmpty(savestate.id))
                            throw new InvalidDataException("Unsupported or incomplete savestate.");
                        _savestates.Add(savestate);
                    }
                    catch (Exception ex)
                    {
                        Logger.LogWarning("Savestate rejected " + Path.GetFileName(path) + ": " + ex.Message);
                        Quarantine(path);
                    }
                }
                _savestates.Sort((a, b) => string.CompareOrdinal(b.createdUtc, a.createdUtc));
            }
            catch (Exception ex) { Logger.LogError("Could not load savestates: " + ex); }
        }

        private void SaveRecording(RecordingDefinition recording)
        {
            WriteJsonAtomic(Path.Combine(_recordingsPath, SafeId(recording.id) + ".json"), recording, false);
        }

        private static void ValidateLibrary(MacroLibrary library)
        {
            if (library == null) throw new InvalidDataException("File contains no library.");
            if (library.schemaVersion != MacroLibrary.CurrentSchemaVersion)
                throw new InvalidDataException("Unsupported schema " + library.schemaVersion + "; expected " + MacroLibrary.CurrentSchemaVersion + ".");
            if (library.macros == null)
                throw new InvalidDataException("Required route collection is missing.");
            foreach (MacroDefinition route in library.macros)
                if (route == null || string.IsNullOrEmpty(route.id) || route.steps == null)
                    throw new InvalidDataException("A route is incomplete.");
        }

        private static void ValidateRecording(RecordingDefinition recording)
        {
            if (recording == null) throw new InvalidDataException("File contains no recording.");
            if (recording.schemaVersion != RecordingDefinition.CurrentSchemaVersion)
                throw new InvalidDataException("Unsupported schema " + recording.schemaVersion + "; expected " + RecordingDefinition.CurrentSchemaVersion + ".");
            if (string.IsNullOrEmpty(recording.id) || recording.samples == null)
                throw new InvalidDataException("Recording is incomplete.");
        }

        private static bool NormalizeLibraryFrameTiming(MacroLibrary library)
        {
            bool changed = false;
            int tickMs = Mathf.Max(1, Mathf.RoundToInt(Time.fixedDeltaTime * 1000f));
            foreach (MacroDefinition route in library.macros)
                foreach (MacroStep step in route.steps) changed |= NormalizeStepFrameTiming(step, tickMs);
            return changed;
        }

        /// <summary>Old routes timed a step in milliseconds (<see cref="ActionEndCondition.Duration"/>, durationMs, timeoutMs):
        /// MacroEngine counts physics ticks only, so they become ticks once, at load.</summary>
        private static bool NormalizeStepFrameTiming(MacroStep step, int tickMs)
        {
            if (step == null) return false;
            bool changed = false;
            if (step.endCondition == ActionEndCondition.Duration)
            {
                step.durationTicks = Mathf.Max(1, Mathf.RoundToInt(step.durationMs / (float)tickMs));
                step.endCondition = ActionEndCondition.FixedTicks;
                changed = true;
            }
            else if (step.durationTicks < 1) { step.durationTicks = 1; changed = true; }
            if (step.timeoutTicks < 1)
            {
                step.timeoutTicks = Mathf.Max(1, Mathf.RoundToInt(step.timeoutMs / (float)tickMs));
                changed = true;
            }
            return changed;
        }

        private static void Quarantine(string path)
        {
            if (!File.Exists(path)) return;
            string rejected = path + ".rejected-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
            try { File.Move(path, rejected); }
            catch (Exception ex) { if (Log != null) Log.LogError("Could not quarantine " + path + ": " + ex.Message); }
        }

        private void SaveLibrary()
        {
            WriteJsonAtomic(_libraryPath, _library, true);
            if (_settings.selectedRouteId != (SelectedMacro?.id ?? "")) SaveSelection();
        }

        /// <summary>The open TAS session's route (TasPlugin.Editor.cs TasJournal): written on every edit instead of routes.json.</summary>
        private string TasJournalPath { get { return Path.Combine(Path.GetDirectoryName(_libraryPath), "tas-session.json"); } }

        private void WriteTasJournal(MacroDefinition route)
        {
            try
            {
                string temporary = TasJournalPath + ".tmp";
                File.WriteAllText(temporary, JsonConvert.SerializeObject(route));
                ReplaceAtomic(temporary, TasJournalPath);
            }
            catch (Exception ex) { Logger.LogError("Could not write the TAS session journal: " + ex); }
        }

        private void DeleteTasJournal()
        {
            try { if (File.Exists(TasJournalPath)) File.Delete(TasJournalPath); }
            catch (Exception ex) { Logger.LogError("Could not delete the TAS session journal: " + ex); }
        }

        /// <summary>A session the game stopped before closing: its journaled route replaces the library's copy (by id).</summary>
        private bool RecoverTasJournal()
        {
            if (!File.Exists(TasJournalPath)) return false;
            try
            {
                MacroDefinition route = JsonConvert.DeserializeObject<MacroDefinition>(File.ReadAllText(TasJournalPath));
                ValidateLibrary(new MacroLibrary { macros = new List<MacroDefinition> { route } });
                int index = _library.macros.FindIndex(r => r.id == route.id);
                if (index >= 0) _library.macros[index] = route;
                else _library.macros.Add(route);
                Logger.LogInfo("TAS session journal recovered: " + route.name + " (" + route.steps.Count + " steps)");
                return true;
            }
            catch (Exception ex)
            {
                Logger.LogError("TAS session journal rejected: " + ex.Message);
                Quarantine(TasJournalPath);
                return false;
            }
        }

        /// <summary>
        /// The selected route alone goes to settings.json (routes.json is the whole library, many megabytes): LoadLibrary
        /// selects it over the library's selectedIndex, and SaveLibrary keeps the two the same.
        /// </summary>
        private void SaveSelection()
        {
            _settings.selectedRouteId = SelectedMacro?.id ?? "";
            SaveSettings();
        }

        private void WriteJsonAtomic(string path, object value, bool keepBackup)
        {
            BumpWebState();
            try
            {
                string directory = Path.GetDirectoryName(path);
                Directory.CreateDirectory(directory);
                string temporary = path + ".tmp";
                File.WriteAllText(temporary, JsonConvert.SerializeObject(value, Formatting.Indented));
                ReplaceAtomic(temporary, path, keepBackup ? path + ".bak" : null);
            }
            catch (Exception ex) { Logger.LogError("Could not save " + path + ": " + ex); }
        }

        /// <summary>A file written aside (<paramref name="temporary"/>) moved over <paramref name="path"/>: a crash mid-write
        /// never leaves it half written.</summary>
        static void ReplaceAtomic(string temporary, string path, string backup = null)
        {
            if (File.Exists(path)) File.Replace(temporary, path, backup);
            else File.Move(temporary, path);
        }

        private static string SafeId(string id)
        {
            if (string.IsNullOrEmpty(id)) throw new InvalidDataException("A stable ID is required.");
            foreach (char invalid in Path.GetInvalidFileNameChars()) id = id.Replace(invalid, '_');
            return id;
        }
    }

    /// <summary>Files parsed once per version, by path: <see cref="Get"/> parses a file again only when its write time
    /// changed. What it returns is shared, for reading only.</summary>
    internal sealed class CachedFiles<T> : Dictionary<string, (DateTime written, T value)>
    {
        public CachedFiles() { }
        public CachedFiles(IEqualityComparer<string> comparer) : base(comparer) { }

        public T Get(string path, Func<string, T> parse)
        {
            DateTime written = File.GetLastWriteTimeUtc(path);
            if (TryGetValue(path, out var cached) && cached.written == written) return cached.value;
            T value = parse(path);
            this[path] = (written, value);
            return value;
        }
    }
}
