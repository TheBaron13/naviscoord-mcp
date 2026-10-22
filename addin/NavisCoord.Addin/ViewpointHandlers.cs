using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Navisworks.Api;

namespace NavisCoord
{
    /// <summary>
    /// <c>viewpoints/list</c> and <c>viewpoints/apply</c>.
    /// </summary>
    /// <remarks>
    /// <c>viewpoints/save</c> (in <see cref="WriteHandlers"/>) only ever
    /// writes — one viewpoint per issue, for a human to click through inside
    /// Navisworks afterwards. Nothing reads them back through the bridge, so a
    /// caller that wants to return to a saved camera, or hand a named
    /// viewpoint to another tool, could not: the round trip was half built.
    /// </remarks>
    internal static class ViewpointHandlers
    {
        public static Dictionary<string, object> ListViewpoints(Dictionary<string, object> payload)
        {
            var doc = Router.RequireDocument();
            var items = new List<object>();
            Walk(doc.SavedViewpoints.RootItem, new List<string>(), items);
            return new Dictionary<string, object> { ["viewpoints"] = items, ["count"] = (double)items.Count };
        }

        private static void Walk(GroupItem folder, List<string> path, List<object> into)
        {
            if (folder == null) return;
            foreach (var child in folder.Children)
            {
                if (child == null) continue;
                if (child is GroupItem group)
                {
                    var next = new List<string>(path) { child.DisplayName ?? string.Empty };
                    Walk(group, next, into);
                    continue;
                }
                if (!(child is SavedViewpoint saved)) continue;
                into.Add(new Dictionary<string, object>
                {
                    ["name"] = saved.DisplayName ?? string.Empty,
                    ["guid"] = saved.Guid.ToString(),
                    ["folder"] = string.Join(" / ", path),
                    ["is_group"] = false
                });
            }
        }

        /// <summary>Finds a saved viewpoint by GUID first, then by exact name.</summary>
        /// <remarks>
        /// Names are not unique across folders — two coordinators can both
        /// save "Plan overview" in their own subfolder — so a name match
        /// reports how many it found and which one it used (the first,
        /// depth-first) rather than silently picking one of several.
        /// </remarks>
        private static SavedViewpoint Find(Document doc, string guid, string name, out int nameMatches)
        {
            SavedViewpoint byGuid = null;
            SavedViewpoint firstByName = null;
            var matchCount = 0;

            void Visit(GroupItem folder)
            {
                foreach (var child in folder.Children)
                {
                    if (child == null) continue;
                    if (child is GroupItem group) { Visit(group); continue; }
                    if (!(child is SavedViewpoint saved)) continue;

                    if (!string.IsNullOrEmpty(guid) &&
                        string.Equals(saved.Guid.ToString(), guid, StringComparison.OrdinalIgnoreCase))
                    {
                        byGuid = saved;
                    }
                    if (!string.IsNullOrEmpty(name) &&
                        string.Equals(saved.DisplayName, name, StringComparison.Ordinal))
                    {
                        matchCount++;
                        if (firstByName == null) firstByName = saved;
                    }
                }
            }
            Visit(doc.SavedViewpoints.RootItem);

            if (byGuid != null)
            {
                nameMatches = 1;
                return byGuid;
            }
            nameMatches = matchCount;
            return firstByName;
        }

        public static Dictionary<string, object> ApplyViewpoint(Dictionary<string, object> payload)
        {
            var refused = WriteHandlers.MutationPreflight("viewpoints/apply", payload);
            if (refused != null) return refused;

            var doc = Router.RequireDocument();
            var guid = Json.Str(payload, "guid");
            var name = Json.Str(payload, "name");
            var dryRun = Json.Bool(payload, "dry_run", true);

            if (string.IsNullOrWhiteSpace(guid) && string.IsNullOrWhiteSpace(name))
            {
                throw new InvalidOperationException("Indica 'guid' o 'name' para elegir qué punto de vista aplicar.");
            }

            var found = Find(doc, guid, name, out var nameMatches);
            if (found == null)
            {
                throw new InvalidOperationException(
                    string.IsNullOrEmpty(guid)
                        ? "Ningún punto de vista guardado se llama '" + name + "'."
                        : "Ningún punto de vista guardado tiene el guid '" + guid + "'.");
            }
            if (string.IsNullOrEmpty(guid) && nameMatches > 1)
            {
                // Not applied: the caller is told the name is ambiguous rather
                // than having the first match picked silently on their behalf.
                var liveFingerprint = DocumentContext.Fingerprint(doc);
                var result = new MutationResult("viewpoints/apply")
                {
                    FingerprintBefore = liveFingerprint,
                    FingerprintAfter = liveFingerprint,
                    VerificationSource = VerificationSources.NotApplicable
                };
                result.Fail("'" + name + "' nombra " + nameMatches + " puntos de vista distintos; " +
                            "usa 'guid' (de navis_list_viewpoints) para no depender del orden.");
                var ambiguous = result.ToJson();
                ambiguous["status"] = "failed";
                ambiguous["error"] = "ambiguous_name";
                ambiguous["matches"] = (double)nameMatches;
                return ambiguous;
            }

            if (dryRun)
            {
                return WriteHandlers.Sealed("viewpoints/apply", payload, new Dictionary<string, object>
                {
                    ["would_apply"] = found.DisplayName ?? string.Empty,
                    ["guid"] = found.Guid.ToString()
                },
                requested: 1, applied: 0, verified: 0,
                verificationSource: VerificationSources.NotApplicable, dryRun: true);
            }

            var target = found.Viewpoint;
            doc.ActiveView.CopyViewpointFrom(target, ViewChange.JumpCut);

            // Re-read the live camera and compare by position: CopyViewpointFrom
            // not throwing proves nothing by itself, the same reason every other
            // mutation in this add-in re-reads rather than trusting the call.
            var live = doc.CurrentViewpoint.ToViewpoint();
            var matches = live != null &&
                          Math.Abs(live.Position.X - target.Position.X) < 1e-3 &&
                          Math.Abs(live.Position.Y - target.Position.Y) < 1e-3 &&
                          Math.Abs(live.Position.Z - target.Position.Z) < 1e-3 &&
                          live.Projection == target.Projection;

            return WriteHandlers.Sealed("viewpoints/apply", payload, new Dictionary<string, object>
            {
                ["applied"] = found.DisplayName ?? string.Empty,
                ["guid"] = found.Guid.ToString()
            },
            requested: 1, applied: 1, verified: matches ? 1 : 0,
            verificationSource: VerificationSources.ViewpointReread,
            warnings: !matches ? new[] { "La cámara en vivo no coincide con la guardada tras aplicarla." } : null);
        }
    }
}
