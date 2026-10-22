using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Navisworks.Api;

namespace NavisCoord
{
    /// <summary>
    /// <c>visibility/set</c> and <c>visibility/isolate</c>: hide, show or
    /// isolate specific elements, independent of clash-issue scoring.
    /// </summary>
    /// <remarks>
    /// <see cref="VisualScope"/> already hides and isolates elements, but only
    /// as a scope that restores itself when one render finishes — it exists so
    /// a picture can fade the context and put everything back, never so an
    /// operator can hide a duct and leave it hidden. These routes are the
    /// other thing: a deliberate, lasting change the caller asked for by name.
    ///
    /// No blanket "show everything" route exists on purpose. `appearance/reset`
    /// already learned this lesson once: an empty call used to mean "delete
    /// every override in the model", including ones a person put there by hand,
    /// and the fix was refusing to guess. The same hazard applies here — a
    /// "reveal the whole federation" button would just as happily undo a
    /// coordinator's own manual hides — so un-hiding is `visibility/set` with
    /// `hidden=false` over the SAME `path_ids` a prior call named (isolate
    /// returns exactly the ids it hid, for precisely this).
    /// </remarks>
    internal static class VisibilityHandlers
    {
        /// <summary>
        /// Children of one parent isolation is willing to walk.
        /// </summary>
        /// <remarks>
        /// Same limit and same reason as <see cref="VisualScope"/>'s: a CAD
        /// import can hang hundreds of thousands of entities off one node, and
        /// walking that per call turns a quick isolate into a held UI thread.
        /// </remarks>
        private const int SiblingLimit = 20000;

        public static Dictionary<string, object> SetVisibility(Dictionary<string, object> payload)
        {
            var refused = WriteHandlers.MutationPreflight("visibility/set", payload);
            if (refused != null) return refused;

            var doc = Router.RequireDocument();
            var pathIds = Json.StrArr(payload, "path_ids");
            var hidden = Json.Bool(payload, "hidden", true);
            var dryRun = Json.Bool(payload, "dry_run", true);

            if (pathIds.Count == 0)
            {
                throw new InvalidOperationException(
                    "'path_ids' no puede estar vacío: indica qué elementos ocultar o mostrar.");
            }

            var unique = pathIds.Distinct(StringComparer.Ordinal).Count();
            var items = NavisContext.ResolveMany(doc, pathIds);
            if (items.Count == 0)
            {
                throw new InvalidOperationException("Ningún elemento se pudo resolver a partir de 'path_ids'.");
            }

            if (dryRun)
            {
                return WriteHandlers.Sealed("visibility/set", payload, new Dictionary<string, object>
                {
                    ["action"] = hidden ? "hide" : "show",
                    ["would_affect"] = (double)items.Count
                },
                requested: unique, applied: 0, verified: 0,
                verificationSource: VerificationSources.NotApplicable, dryRun: true);
            }

            doc.Models.SetHidden(items, hidden);

            // Re-read per item rather than trusting the call returned. The
            // document-level query is what VisualScope found reliable where
            // the per-item property throws on most of a real tree.
            var confirmed = 0;
            foreach (var item in items)
            {
                bool? nowHidden;
                try { nowHidden = doc.Models.IsHidden(new[] { item }); }
                catch { nowHidden = null; }
                if (nowHidden == hidden) confirmed++;
            }

            return WriteHandlers.Sealed("visibility/set", payload, new Dictionary<string, object>
            {
                ["action"] = hidden ? "hide" : "show",
                ["path_ids"] = pathIds,
                ["unique_requested"] = (double)unique,
                ["resolved"] = (double)items.Count
            },
            requested: unique, applied: items.Count, verified: confirmed,
            verificationSource: VerificationSources.VisibilityStateReread,
            warnings: confirmed < items.Count
                ? new[] { "La relectura no confirma el nuevo estado de " + (items.Count - confirmed) +
                          " elemento(s); IsHidden no siempre puede leerlos." }
                : null);
        }

        /// <summary>
        /// Hides every sibling of the ancestor chain of <c>path_ids</c>,
        /// leaving only those elements (and their descendants) visible.
        /// </summary>
        /// <remarks>
        /// Navisworks has no isolate call. The construction here is the one a
        /// person performs by hand — walk the ancestor chain of what must
        /// survive and hide each ancestor's OTHER children — and it is a
        /// smaller, standalone copy of <see cref="VisualScope.Isolate"/>'s
        /// algorithm: that one is wired into a disposable scope that restores
        /// on its own, which is the wrong lifetime for a result the caller
        /// means to keep.
        /// </remarks>
        public static Dictionary<string, object> Isolate(Dictionary<string, object> payload)
        {
            var refused = WriteHandlers.MutationPreflight("visibility/isolate", payload);
            if (refused != null) return refused;

            var doc = Router.RequireDocument();
            var pathIds = Json.StrArr(payload, "path_ids");
            var dryRun = Json.Bool(payload, "dry_run", true);

            if (pathIds.Count == 0)
            {
                throw new InvalidOperationException(
                    "'path_ids' no puede estar vacío: indica qué elementos quedan visibles.");
            }

            var unique = pathIds.Distinct(StringComparer.Ordinal).Count();
            var keep = NavisContext.ResolveMany(doc, pathIds);
            if (keep.Count == 0)
            {
                throw new InvalidOperationException("Ningún elemento se pudo resolver a partir de 'path_ids'.");
            }

            var survivors = new HashSet<ModelItem>();
            var walked = 0;
            foreach (var item in keep)
            {
                if (item == null) continue;
                try
                {
                    foreach (var ancestor in item.AncestorsAndSelf) survivors.Add(ancestor);
                    foreach (var descendant in item.Descendants) survivors.Add(descendant);
                    walked++;
                }
                catch
                {
                    // Detached from the live tree: this one contributes nothing
                    // to isolate, same as a clash side that cannot be anchored.
                }
            }
            if (walked == 0)
            {
                throw new InvalidOperationException(
                    "Ninguno de los elementos resueltos está en el árbol del documento: no hay nada que aislar.");
            }

            var toHide = new ModelItemCollection();
            var tooWide = false;
            foreach (var item in keep)
            {
                if (item == null) continue;
                try
                {
                    foreach (var ancestor in item.AncestorsAndSelf)
                    {
                        var siblings = 0;
                        foreach (var sibling in ancestor.Children)
                        {
                            if (++siblings > SiblingLimit) { tooWide = true; break; }
                            if (sibling == null) continue;
                            if (survivors.Contains(sibling)) continue;
                            if (toHide.Contains(sibling)) continue;
                            toHide.Add(sibling);
                        }
                    }
                }
                catch
                {
                    // Same tolerance as the walk above.
                }
            }

            var hiddenPathIds = toHide.Select(item => NavisContext.PathId(doc, item)).ToList();

            if (dryRun)
            {
                return WriteHandlers.Sealed("visibility/isolate", payload, new Dictionary<string, object>
                {
                    ["would_hide"] = (double)toHide.Count,
                    ["would_keep"] = (double)keep.Count,
                    ["sibling_limit_reached"] = tooWide
                },
                requested: unique, applied: 0, verified: 0,
                verificationSource: VerificationSources.NotApplicable, dryRun: true);
            }

            if (toHide.Count > 0) doc.Models.SetHidden(toHide, true);

            var confirmed = 0;
            foreach (var item in toHide)
            {
                bool? nowHidden;
                try { nowHidden = doc.Models.IsHidden(new[] { item }); }
                catch { nowHidden = null; }
                if (nowHidden == true) confirmed++;
            }

            return WriteHandlers.Sealed("visibility/isolate", payload, new Dictionary<string, object>
            {
                ["kept"] = (double)keep.Count,
                ["hidden"] = (double)toHide.Count,
                ["hidden_path_ids"] = hiddenPathIds,
                ["sibling_limit_reached"] = tooWide
            },
            requested: unique, applied: toHide.Count, verified: confirmed,
            verificationSource: VerificationSources.VisibilityStateReread,
            warnings: confirmed < toHide.Count
                ? new[] { "La relectura no confirma el ocultamiento de " + (toHide.Count - confirmed) + " elemento(s)." }
                : null);
        }
    }
}
