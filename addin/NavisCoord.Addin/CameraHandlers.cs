using System;
using System.Collections.Generic;
using Autodesk.Navisworks.Api;

namespace NavisCoord
{
    /// <summary>
    /// <c>view/set_camera</c>: explicit camera placement, as its own tool.
    /// </summary>
    /// <remarks>
    /// The position/aim/up math this needs already exists inside
    /// <see cref="ReportHandlers"/>, applied there to frame one clash image and
    /// restored immediately after by <see cref="VisualScope"/>. There was no
    /// way to move the camera and LEAVE it there — every existing route that
    /// touches the viewpoint either saves one for later (viewpoints/save) or
    /// changes it only for the lifetime of a single render.
    /// </remarks>
    internal static class CameraHandlers
    {
        public static Dictionary<string, object> SetCamera(Dictionary<string, object> payload)
        {
            var refused = WriteHandlers.MutationPreflight("view/set_camera", payload);
            if (refused != null) return refused;

            var doc = Router.RequireDocument();
            var dryRun = Json.Bool(payload, "dry_run", true);

            var position = Json.Arr(payload, "position");
            if (position.Count != 3)
            {
                throw new InvalidOperationException("'position' debe ser [x, y, z] en las unidades del documento.");
            }
            var target = Json.Arr(payload, "look_at");
            if (target.Count != 3)
            {
                throw new InvalidOperationException("'look_at' debe ser [x, y, z]: el punto hacia el que mira la cámara.");
            }
            var up = Json.Arr(payload, "up");
            if (up.Count != 0 && up.Count != 3)
            {
                throw new InvalidOperationException("'up' debe estar vacío (usa el del documento) o ser [x, y, z].");
            }

            var projectionRaw = Json.Str(payload, "projection", "perspective").Trim().ToLowerInvariant();
            if (projectionRaw != "perspective" && projectionRaw != "orthographic")
            {
                throw new InvalidOperationException("'projection' debe ser 'perspective' u 'orthographic'.");
            }

            ViewpointRenderStyle? renderStyle = null;
            var renderStyleRaw = Json.Str(payload, "render_style");
            if (!string.IsNullOrWhiteSpace(renderStyleRaw))
            {
                if (!Enum.TryParse(renderStyleRaw, true, out ViewpointRenderStyle parsed))
                {
                    throw new InvalidOperationException(
                        "'render_style' no reconocido: '" + renderStyleRaw + "'. Usa uno de: " +
                        string.Join(", ", Enum.GetNames(typeof(ViewpointRenderStyle))) + ".");
                }
                renderStyle = parsed;
            }

            var positionPoint = ToPoint(position);
            var targetPoint = ToPoint(target);
            var upVector = ResolveUp(up, doc.CurrentViewpoint.ToViewpoint());

            if (dryRun)
            {
                return WriteHandlers.Sealed("view/set_camera", payload, new Dictionary<string, object>
                {
                    ["would_position"] = position,
                    ["would_look_at"] = target,
                    ["projection"] = projectionRaw
                },
                requested: 1, applied: 0, verified: 0,
                verificationSource: VerificationSources.NotApplicable, dryRun: true);
            }

            Viewpoint viewpoint;
            try
            {
                viewpoint = doc.CurrentViewpoint.ToViewpoint().CreateCopy();
                viewpoint.Projection = projectionRaw == "orthographic"
                    ? ViewpointProjection.Orthographic
                    : ViewpointProjection.Perspective;

                viewpoint.Position = positionPoint;
                // Aim first, then roll — AlignUp before PointAt is undone by
                // the aim, same ordering ReportHandlers uses for exactly this
                // reason.
                viewpoint.PointAt(targetPoint);
                viewpoint.AlignUp(upVector);
                if (renderStyle.HasValue) viewpoint.RenderStyle = renderStyle.Value;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("No se pudo construir el punto de vista pedido: " + ex.Message);
            }

            // Through the VIEW, same as ReportHandlers and VisualScope's
            // restore: it is the object Navisworks itself treats as "the
            // camera", and it is what every other camera move in this add-in
            // goes through.
            doc.ActiveView.CopyViewpointFrom(viewpoint, ViewChange.JumpCut);

            var live = doc.CurrentViewpoint.ToViewpoint();
            var matches = live != null &&
                          Math.Abs(live.Position.X - positionPoint.X) < 1e-3 &&
                          Math.Abs(live.Position.Y - positionPoint.Y) < 1e-3 &&
                          Math.Abs(live.Position.Z - positionPoint.Z) < 1e-3 &&
                          live.Projection == viewpoint.Projection;

            return WriteHandlers.Sealed("view/set_camera", payload, new Dictionary<string, object>
            {
                ["position"] = position,
                ["look_at"] = target,
                ["projection"] = projectionRaw,
                // Documented, not guessed: ReportHandlers measured this on a
                // live federation — moving the camera marks the document
                // modified even though nothing is written to disk, and there
                // is no offscreen camera to avoid it with.
                ["document_marked_modified"] = DocumentContext.SafeIsModified(doc)
            },
            requested: 1, applied: 1, verified: matches ? 1 : 0,
            verificationSource: VerificationSources.ViewpointReread,
            warnings: !matches ? new[] { "La cámara en vivo no coincide con la pedida tras aplicarla." } : null);
        }

        private static double NumAt(List<object> arr, int index)
            => index < arr.Count && arr[index] is double d ? d : 0.0;

        private static Point3D ToPoint(List<object> xyz)
            => new Point3D(NumAt(xyz, 0), NumAt(xyz, 1), NumAt(xyz, 2));

        private static Vector3D ToVector(List<object> xyz)
            => new Vector3D(NumAt(xyz, 0), NumAt(xyz, 1), NumAt(xyz, 2)).Normalize();

        /// <summary>The document's own up, unless the caller supplied one.</summary>
        /// <remarks>
        /// Falling back to +Z outright would tilt every shot on a Y-up
        /// document — the same hazard noted in
        /// <see cref="ClashFraming"/>'s own direction helper.
        /// </remarks>
        private static Vector3D ResolveUp(List<object> up, Viewpoint current)
        {
            if (up.Count == 3) return ToVector(up);
            if (current != null && current.HasWorldUpVector) return current.WorldUpVector.ToVector3D();
            return new Vector3D(0, 0, 1);
        }
    }
}
