using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace IGTAPTasMod
{
    /// <summary>
    /// Sharing routes (/api/tas/share/*): a route with its markers, re-record count and savestate as one JSON document, or as
    /// a code to paste (<c>IGTAS1:</c> + base64url of its gzip), or as TAS text (TasMarkup). An import is a plain route: a
    /// record's name gets " shared" after its category, so the only way it becomes a record again is the gate.
    /// </summary>
    public sealed partial class TasPlugin
    {
        const string ShareFormat = "igtap-tas-route", ShareCodePrefix = "IGTAS1:";

        object HandleShare(string action, JObject body)
        {
            if (action == "export")
            {
                string id = Text(body, "routeId");
                MacroDefinition route = (id.Length > 0 ? _library.macros.Find(r => r.id == id) : _tas?.route)
                    ?? throw new InvalidOperationException("Route not found.");
                if (_tas != null && _tas.route == route) route.steps = ToSteps(_tas.ticks);
                if (!route.steps.All(s => s.type == MacroStepType.RawInput)) throw new InvalidOperationException("Only raw-input routes can be shared.");
                Savestate savestate = string.IsNullOrEmpty(route.savestateId) ? null : _savestates.Find(s => s.id == route.savestateId);
                var doc = new JObject {
                    ["format"] = ShareFormat, ["version"] = 1, ["name"] = route.name,
                    ["steps"] = JArray.FromObject(route.steps), ["markers"] = JArray.FromObject(route.markers ?? new List<RouteMarker>()),
                    ["rerecords"] = route.rerecords, ["savestate"] = savestate == null ? null : JObject.FromObject(savestate),
                    ["game"] = new JObject { ["version"] = Application.version, ["assembly"] = SavestateService.GameAssemblyHash() },
                    ["plugin"] = PluginVersion, ["exportedUtc"] = DateTime.UtcNow.ToString("o") };
                string code = ShareCodePrefix + Base64Url(Gzip(Encoding.UTF8.GetBytes(doc.ToString(Formatting.None))));
                return new { ok = true, name = route.name, code, document = doc, text = TasMarkup.Format(route.name, route.steps, route.markers),
                    fileName = string.Concat(route.name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)) };
            }
            if (action == "import")
            {
                string input = Text(body, "code").Trim();
                if (input.Length == 0) throw new InvalidOperationException("Paste a share code, a shared route's JSON or TAS text.");
                JObject doc;
                if (input.StartsWith(ShareCodePrefix, StringComparison.Ordinal))
                    doc = JObject.Parse(Encoding.UTF8.GetString(Gunzip(FromBase64Url(input.Substring(ShareCodePrefix.Length)))));
                else if (input.StartsWith("{", StringComparison.Ordinal)) doc = JObject.Parse(input);
                else
                {
                    List<MacroStep> parsed = TasMarkup.Parse(input, out List<RouteMarker> textMarkers);
                    string textName = Text(body, "name").Trim();
                    if (textName.Length == 0) textName = TextRouteName(input);
                    doc = new JObject { ["format"] = ShareFormat, ["version"] = 1, ["name"] = textName, ["steps"] = JArray.FromObject(parsed),
                        ["markers"] = JArray.FromObject(textMarkers) };
                }
                if ((string)doc["format"] != ShareFormat) throw new InvalidOperationException("Not a shared IGTAP TAS route.");
                if (((int?)doc["version"] ?? 0) > 1) throw new InvalidOperationException("This route was shared by a newer TAS Studio.");
                List<MacroStep> steps = doc["steps"]?.ToObject<List<MacroStep>>() ?? new List<MacroStep>();
                if (steps.Count == 0 || !steps.All(s => s.type == MacroStepType.RawInput)) throw new InvalidOperationException("The shared route has no raw inputs.");
                if (steps.Sum(s => (long)Math.Max(1, s.durationTicks)) > TasMarkup.MaxTicks)
                    throw new InvalidOperationException("The shared route is longer than " + TasMarkup.MaxTicks + " input ticks.");
                steps = steps.Where(s => s.enabled).ToList();
                // Its ticks as the editor keeps them: controller sticks only (RULES.md 2).
                steps = ToSteps(ToTicks(steps).Select(TasSanitize).ToList());
                var route = new MacroDefinition { name = TasUniqueName(SharedName(((string)doc["name"] ?? "shared").Trim())), steps = steps,
                    markers = doc["markers"]?.ToObject<List<RouteMarker>>(), rerecords = (int?)doc["rerecords"] ?? 0 };
                RejectCategoryName(route.name);
                if (doc["savestate"] is JObject shared)
                {
                    Savestate savestate = shared.ToObject<Savestate>();
                    // Only what a capture holds: no unlocks (they would stay on the save and be autosaved) or other fields.
                    SavestateService.KeepCapturedFields(savestate);
                    savestate.id = Guid.NewGuid().ToString("N");
                    savestate.name = "shared/" + savestate.name;
                    _savestates.Insert(0, savestate);
                    WriteJsonAtomic(Path.Combine(_savestatesPath, SafeId(savestate.id) + ".json"), savestate, false);
                    route.savestateId = savestate.id;
                }
                _library.macros.Add(route);
                SaveLibrary();
                string assembly = (string)doc["game"]?["assembly"];
                string warning = assembly != null && assembly != SavestateService.GameAssemblyHash()
                    ? "Shared from another game build (" + (string)doc["game"]["version"] + "): it may not play the same." : null;
                return new { ok = true, routeId = route.id, name = route.name, warning };
            }
            throw new InvalidOperationException("Unknown share command: " + action);
        }

        /// <summary>A record's name ("4_WJ_DS 13.28", "4_WJ_DS farm 13.62") as a plain route of the same category.</summary>
        static string SharedName(string name)
        {
            var record = MatchGatedRoute(name);
            return record.Success ? record.Groups["category"].Value + " shared " + name.Substring(record.Groups["category"].Length).Trim() : name;
        }

        static string TextRouteName(string text)
        {
            string first = text.Replace("\r", "").Split('\n').FirstOrDefault(l => l.StartsWith("# IGTAP TAS:", StringComparison.Ordinal));
            return first != null ? first.Substring("# IGTAP TAS:".Length).Trim() : "tas text";
        }

        static byte[] Gzip(byte[] data)
        {
            using (var output = new MemoryStream())
            {
                using (var gzip = new GZipStream(output, System.IO.Compression.CompressionLevel.Optimal)) gzip.Write(data, 0, data.Length);
                return output.ToArray();
            }
        }

        const int ShareMaxBytes = 16 << 20;

        static byte[] Gunzip(byte[] data)
        {
            using (var input = new GZipStream(new MemoryStream(data), CompressionMode.Decompress))
            using (var output = new MemoryStream())
            {
                var buffer = new byte[81920];
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    if (output.Length + read > ShareMaxBytes) throw new InvalidOperationException("The share code unpacks to more than 16 MB.");
                    output.Write(buffer, 0, read);
                }
                return output.ToArray();
            }
        }

        static string Base64Url(byte[] data) { return Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_'); }

        static byte[] FromBase64Url(string text)
        {
            string b64 = text.Trim().Replace('-', '+').Replace('_', '/');
            return Convert.FromBase64String(b64 + new string('=', (4 - b64.Length % 4) % 4));
        }
    }
}
