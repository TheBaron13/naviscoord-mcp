using System;
using System.Collections.Generic;
using System.IO;
using Autodesk.Navisworks.Api;

namespace NavisCoord
{
    /// <summary>
    /// <c>document/open</c> and <c>document/append</c>.
    /// </summary>
    /// <remarks>
    /// Every route in this add-in until now assumed a human had already
    /// opened something in Navisworks — read it, coordinated it, saved it,
    /// closed it. Nothing could LOAD a federation in the first place, which
    /// meant every unattended or scripted run still needed someone at the
    /// machine for the one step before any of this is reachable.
    ///
    /// Opening replaces whatever is loaded, so it follows exactly the same
    /// disposition discipline as <see cref="CloseHandlers"/>: unsaved changes
    /// are never discarded by implication, only by a caller naming
    /// <c>disposition=discard</c> out loud. Appending adds to what is already
    /// open, so it is an ordinary fingerprinted mutation like any other.
    /// </remarks>
    internal static class OpenFileHandlers
    {
        public static Dictionary<string, object> OpenFile(Dictionary<string, object> payload)
            => OpenFile(payload, null);

        public static Dictionary<string, object> OpenFile(Dictionary<string, object> payload, JobManager.Job job)
        {
            var doc = Autodesk.Navisworks.Api.Application.ActiveDocument;
            var open = doc != null && !doc.IsClear;
            var fingerprintBefore = open ? DocumentContext.Fingerprint(doc) : string.Empty;

            var result = new MutationResult("document/open")
            {
                JobId = job?.Id ?? string.Empty,
                FingerprintBefore = fingerprintBefore,
                FingerprintAfter = fingerprintBefore,
                Requested = 1,
                DryRun = Json.Bool(payload, "dry_run", true),
                VerificationSource = VerificationSources.DocumentLoadReread
            };

            var path = Json.Str(payload, "path");
            if (string.IsNullOrWhiteSpace(path))
            {
                result.Fail("document/open necesita 'path'.");
                return result.ToJson();
            }
            if (!File.Exists(path))
            {
                result.Fail("No existe el archivo '" + path + "'.");
                return result.ToJson();
            }

            // Only matters while something is actually loaded: an empty
            // document has nothing for the fingerprint to protect.
            if (open)
            {
                var expected = Json.Str(payload, "expected_document_fingerprint");
                if (string.IsNullOrWhiteSpace(expected))
                {
                    result.Fail(
                        "Hay un documento cargado y falta 'expected_document_fingerprint'. " +
                        "Ábrelo solo sobre el documento que esperas reemplazar; léelo de 'health'.");
                    return result.ToJson();
                }
                if (!DocumentFingerprint.Matches(expected, fingerprintBefore))
                {
                    result.Fail(
                        "El documento activo no es el que esperabas (esperado " + expected +
                        ", activo " + fingerprintBefore + "). No se abrió nada.");
                    return result.ToJson();
                }
            }

            var modified = open && DocumentContext.SafeIsModified(doc);
            var disposition = ClosePolicy.Normalize(Json.Str(payload, "disposition"));
            // Nothing is open: there is nothing to save or discard, so the
            // disposition choice is moot and not required.
            var problem = open ? ClosePolicy.Problem(disposition, modified) : string.Empty;
            result.Detail["disposition"] = disposition;
            result.Detail["was_modified"] = modified;
            result.Detail["document_open_before"] = open;
            if (!string.IsNullOrEmpty(problem))
            {
                result.Fail(problem);
                return result.ToJson();
            }

            if (result.DryRun)
            {
                result.Detail["would_open"] = path;
                result.Detail["would_save_first"] = modified && disposition == ClosePolicy.Save;
                result.Detail["would_discard_changes"] = modified && disposition == ClosePolicy.Discard;
                return result.ToJson();
            }

            if (modified && disposition == ClosePolicy.Save)
            {
                var savePayload = new Dictionary<string, object>(payload) { ["dry_run"] = false };
                var save = SaveHandlers.Save(savePayload);
                result.Detail["save"] = save;
                if (!string.Equals(Json.Str(save, "status"), "completed", StringComparison.OrdinalIgnoreCase) ||
                    Json.Int(save, "verified") < 1)
                {
                    result.Fail("El guardado previo no quedó verificado; no se abrió el nuevo archivo.");
                    return result.ToJson();
                }
            }
            else if (modified && disposition == ClosePolicy.Discard)
            {
                result.Warn("Se descartaron cambios sin guardar porque el llamador eligió disposition=discard.");
            }

            job?.Phasing("abriendo", "Cargando " + Path.GetFileName(path));

            bool opened;
            try
            {
                opened = Autodesk.Navisworks.Api.Application.ActiveDocument.TryOpenFile(path);
            }
            catch (Exception ex)
            {
                result.Fail("Navisworks no pudo abrir '" + path + "': " + ex.Message);
                result.FingerprintAfter = DocumentContext.Fingerprint(Autodesk.Navisworks.Api.Application.ActiveDocument);
                return result.ToJson();
            }

            var after = Autodesk.Navisworks.Api.Application.ActiveDocument;
            var loaded = after != null && !after.IsClear;
            result.Applied = opened ? 1 : 0;
            result.Verified = (opened && loaded) ? 1 : 0;
            result.FingerprintAfter = DocumentContext.Fingerprint(after);
            result.Detail["document_open_after"] = loaded;
            result.Detail["title"] = loaded ? after.Title : string.Empty;
            result.Detail["models"] = loaded ? (double)after.Models.Count : 0.0;
            if (!opened) result.Fail("TryOpenFile devolvió falso: Navisworks no cargó '" + path + "'.");
            else if (!loaded) result.Fail("Navisworks reportó éxito pero el documento sigue vacío tras abrir.");
            return result.ToJson();
        }

        public static Dictionary<string, object> AppendFile(Dictionary<string, object> payload)
            => AppendFile(payload, null);

        public static Dictionary<string, object> AppendFile(Dictionary<string, object> payload, JobManager.Job job)
        {
            var refused = WriteHandlers.MutationPreflight("document/append", payload);
            if (refused != null) return refused;

            var doc = Router.RequireDocument();
            var path = Json.Str(payload, "path");
            var dryRun = Json.Bool(payload, "dry_run", true);
            var idempotencyKey = Json.Str(payload, "idempotency_key");
            var fingerprintBefore = DocumentContext.Fingerprint(doc);

            if (string.IsNullOrWhiteSpace(path))
            {
                throw new InvalidOperationException("document/append necesita 'path'.");
            }
            if (!File.Exists(path))
            {
                throw new InvalidOperationException("No existe el archivo '" + path + "'.");
            }

            if (dryRun)
            {
                return WriteHandlers.Sealed("document/append", payload, new Dictionary<string, object>
                {
                    ["would_append"] = path,
                    ["models_before"] = (double)doc.Models.Count
                },
                requested: 1, applied: 0, verified: 0,
                verificationSource: VerificationSources.NotApplicable, dryRun: true);
            }

            // Scoped to operation + document: appending the same file twice
            // because a retry fired is a duplicated model in the federation,
            // not a harmless no-op, which is why this route checks the ledger
            // and document/save_as's sibling routes mostly do not need to.
            if (IdempotencyLedger.TryGet(idempotencyKey, "document/append", fingerprintBefore, out var replay))
            {
                return replay;
            }

            job?.Phasing("anexando", "Cargando " + Path.GetFileName(path));

            var before = doc.Models.Count;
            bool appended;
            try
            {
                appended = doc.TryAppendFile(path);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Navisworks no pudo anexar '" + path + "': " + ex.Message);
            }

            var after = doc.Models.Count;
            var gained = Math.Max(0, after - before);

            var sealedResult = WriteHandlers.Sealed("document/append", payload, new Dictionary<string, object>
            {
                ["path"] = path,
                ["models_before"] = (double)before,
                ["models_after"] = (double)after,
                ["models_gained"] = (double)gained
            },
            requested: 1, applied: appended ? 1 : 0, verified: gained > 0 ? 1 : 0,
            verificationSource: VerificationSources.DocumentLoadReread,
            warnings: appended && gained == 0
                ? new[] { "TryAppendFile devolvió verdadero pero el recuento de modelos no subió." }
                : null);

            if (Json.Str(sealedResult, "status") == "completed")
            {
                IdempotencyLedger.Remember(idempotencyKey, sealedResult);
            }
            return sealedResult;
        }
    }
}
