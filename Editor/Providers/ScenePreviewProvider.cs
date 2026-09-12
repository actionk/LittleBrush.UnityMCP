using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using LittleBrushGames.Mcp.Editor.Dispatch;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace LittleBrushGames.Mcp.Editor.Providers
{
    [McpToolProvider]
    public sealed class ScenePreviewProvider : IToolProvider
    {
        public string Namespace => "scene";

        public void RegisterTools(IToolRegistration reg)
        {
            reg.Register(new ToolDescriptor
            {
                Name = "scene.preview_screenshot",
                Description = "Edit Mode only: render a loaded or project scene GameObject without changing the loaded scene set. Uses a temporary camera copied from the gameplay camera, auto-frames renderer bounds, and returns an inline PNG. Supports isolation or scene context; never starts Play Mode.",
                Availability = ToolAvailability.EditMode,
                Execution = ToolExecution.Sync,
                RequiresMainThread = true,
                Timeout = TimeSpan.FromSeconds(20),
                InputSchema = JObject.Parse(@"{
                    ""type"": ""object"",
                    ""properties"": {
                        ""scenePath"": { ""type"": ""string"" },
                        ""path"": { ""type"": ""string"" },
                        ""entityId"": { ""type"": ""string"" },
                        ""cameraPath"": { ""type"": ""string"" },
                        ""cameraPosition"": { ""type"": ""array"", ""minItems"": 3, ""maxItems"": 3, ""items"": { ""type"": ""number"" } },
                        ""cameraRotation"": { ""type"": ""array"", ""minItems"": 3, ""maxItems"": 3, ""items"": { ""type"": ""number"" }, ""description"": ""World Euler angles in degrees. Without cameraPosition, auto-frame using this rotation."" },
                        ""mode"": { ""type"": ""string"", ""enum"": [""isolation"", ""context""], ""description"": ""isolation hides the world; context preserves the scene culling mask."" },
                        ""width"": { ""type"": ""integer"", ""minimum"": 64, ""maximum"": 4096 },
                        ""height"": { ""type"": ""integer"", ""minimum"": 64, ""maximum"": 4096 },
                        ""padding"": { ""type"": ""number"", ""minimum"": 0, ""maximum"": 1 },
                        ""background"": { ""type"": ""string"", ""description"": ""Optional HTML color. Omit to preserve the gameplay camera clear settings."" }
                    }
                }"),
                Handler = Capture,
            });
        }

        private static ValueTask<ToolResult> Capture(ToolContext ctx, CancellationToken _)
        {
            if (EditorApplication.isPlaying)
                throw new McpToolException(McpErrorCodes.ToolUnavailable, "scene.preview_screenshot is Edit Mode only; use editor.screenshot for an explicitly requested Play Mode capture.");

            using var scope = SceneAssetScope.Open((string)ctx.Arguments["scenePath"]);
            var scene = scope.Scene;
            var target = ResolveObject(scene, ctx.Arguments);
            var sourceCamera = ResolveCamera(scene, (string)ctx.Arguments["cameraPath"]);
            var renderers = target.GetComponentsInChildren<Renderer>(true);
            var bounds = GetBounds(target, renderers);
            var mode = (string)ctx.Arguments["mode"] ?? "isolation";
            if (!string.Equals(mode, "isolation", StringComparison.Ordinal) &&
                !string.Equals(mode, "context", StringComparison.Ordinal))
                throw new McpToolException(McpErrorCodes.InvalidParams, "mode must be 'isolation' or 'context'.");
            var isolation = string.Equals(mode, "isolation", StringComparison.Ordinal);
            var width = ctx.Arguments["width"]?.Value<int>() ?? GetDefaultWidth(sourceCamera);
            var height = ctx.Arguments["height"]?.Value<int>() ?? GetDefaultHeight(sourceCamera);
            var padding = ctx.Arguments["padding"]?.Value<float>() ?? 0.1f;
            var background = (string)ctx.Arguments["background"];

            GameObject previewObject = null;
            var hiddenRenderers = new List<Renderer>();
            RenderTexture renderTexture = null;
            Texture2D texture = null;
            try
            {
                previewObject = new GameObject("MCP Scene Object Preview Camera")
                {
                    hideFlags = HideFlags.HideAndDontSave,
                };
                SceneManager.MoveGameObjectToScene(previewObject, scene);
                var previewCamera = previewObject.AddComponent<Camera>();
                CopyCamera(sourceCamera, previewCamera, previewObject);
                // Preview scenes are excluded from ordinary gameplay-camera culling.
                if (scope.OwnsPreviewScene)
                    previewCamera.scene = scene;

                if (isolation)
                {
                    foreach (var root in scene.GetRootGameObjects())
                    {
                        foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
                        {
                            if (!renderer.enabled || renderer.transform.IsChildOf(target.transform))
                                continue;
                            renderer.enabled = false;
                            hiddenRenderers.Add(renderer);
                        }
                    }
                    previewCamera.cullingMask = sourceCamera.cullingMask;
                }
                else
                    previewCamera.cullingMask = sourceCamera.cullingMask;
                previewCamera.enabled = false;

                var framing = ApplyCameraPose(previewCamera, bounds, width, height, padding, ctx.Arguments);
                if (!string.IsNullOrWhiteSpace(background))
                {
                    if (!ColorUtility.TryParseHtmlString(background, out var color))
                        throw new McpToolException(McpErrorCodes.InvalidParams, $"background is not a valid HTML color: '{background}'.");
                    previewCamera.clearFlags = CameraClearFlags.SolidColor;
                    previewCamera.backgroundColor = color;
                }

                renderTexture = RenderTexture.GetTemporary(width, height, 24, RenderTextureFormat.ARGB32);
                var previousTarget = previewCamera.targetTexture;
                var previousActive = RenderTexture.active;
                try
                {
                    previewCamera.targetTexture = renderTexture;
                    previewCamera.Render();
                    RenderTexture.active = renderTexture;
                    texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
                    texture.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                    texture.Apply();
                }
                finally
                {
                    RenderTexture.active = previousActive;
                    previewCamera.targetTexture = previousTarget;
                }

                var png = texture.EncodeToPNG();
                var structured = new JObject
                {
                    ["source"] = "SceneObjectPreview",
                    ["scenePath"] = scene.path,
                    ["objectPath"] = SceneSerializer.GetHierarchyPath(target),
                    ["camera"] = sourceCamera.name,
                    ["width"] = width,
                    ["height"] = height,
                    ["padding"] = padding,
                    ["mode"] = mode,
                    ["isolated"] = isolation,
                    ["nonDestructive"] = true,
                    ["boundsCenter"] = UnitySerializer.ToJson(bounds.center),
                    ["boundsSize"] = UnitySerializer.ToJson(bounds.size),
                    ["frameDistance"] = framing.Distance,
                    ["cameraPosition"] = UnitySerializer.ToJson(previewCamera.transform.position),
                    ["cameraRotation"] = UnitySerializer.ToJson(previewCamera.transform.eulerAngles),
                    ["orthographicSize"] = framing.OrthographicSize,
                };
                return new ValueTask<ToolResult>(ToolResult.Ok(
                    scope.AddMetadata(structured),
                    new ImageContent { Data = png, MimeType = "image/png" }));
            }
            finally
            {
                foreach (var renderer in hiddenRenderers)
                {
                    if (renderer != null)
                        renderer.enabled = true;
                }
                if (texture != null)
                    UnityEngine.Object.DestroyImmediate(texture);
                if (renderTexture != null)
                    RenderTexture.ReleaseTemporary(renderTexture);
                UnityEngine.Object.DestroyImmediate(previewObject);
            }
        }

        private static void CopyCamera(Camera source, Camera target, GameObject targetObject)
        {
            target.CopyFrom(source);
            targetObject.name = source.name + " (MCP Preview)";
            targetObject.tag = "Untagged";

            foreach (var component in source.GetComponents<Component>())
            {
                if (component == null || component is Transform || component is Camera || component is AudioListener)
                    continue;

                var typeName = component.GetType().FullName;
                if (string.IsNullOrEmpty(typeName) || !typeName.EndsWith("AdditionalCameraData", StringComparison.Ordinal))
                    continue;

                var copy = targetObject.AddComponent(component.GetType());
                EditorUtility.CopySerialized(component, copy);
            }
        }

        internal static FrameResult ApplyCameraPose(Camera camera, Bounds bounds, int width, int height, float padding, JObject arguments)
        {
            if (arguments["cameraRotation"] is JArray rotation)
                camera.transform.rotation = Quaternion.Euler(ReadPoseVector(rotation));
            FrameCamera(camera, bounds, width, height, padding);
            if (arguments["cameraPosition"] is JArray position)
                camera.transform.position = ReadPoseVector(position);
            return new FrameResult(Vector3.Distance(camera.transform.position, bounds.center), camera.orthographicSize);
        }

        private static Vector3 ReadPoseVector(JArray value)
        {
            if (value.Count != 3) throw new McpToolException(McpErrorCodes.InvalidParams, "Camera pose requires three finite numbers.");
            var vector = new Vector3((float)value[0], (float)value[1], (float)value[2]);
            if (!float.IsFinite(vector.x) || !float.IsFinite(vector.y) || !float.IsFinite(vector.z))
                throw new McpToolException(McpErrorCodes.InvalidParams, "Camera pose requires three finite numbers.");
            return vector;
        }

        private static FrameResult FrameCamera(Camera camera, Bounds bounds, int width, int height, float padding)
        {
            var rotation = camera.transform.rotation;
            var forward = rotation * Vector3.forward;
            var radius = Mathf.Max(0.001f, bounds.extents.magnitude);
            var paddedRadius = radius * (1f + padding);
            var distance = 0f;
            var orthographicSize = 0f;

            if (camera.orthographic)
            {
                var projected = ProjectBounds(bounds, camera.transform.rotation);
                var aspect = width / (float)Mathf.Max(1, height);
                orthographicSize = Mathf.Max(projected.y, projected.x / Mathf.Max(0.01f, aspect)) *
                                   (1f + padding);
                camera.orthographicSize = orthographicSize;
                distance = Mathf.Max(projected.z + camera.nearClipPlane + 0.1f, paddedRadius);
            }
            else
            {
                var aspect = width / (float)Mathf.Max(1, height);
                var verticalHalfFov = camera.fieldOfView * Mathf.Deg2Rad * 0.5f;
                var horizontalHalfFov = Mathf.Atan(Mathf.Tan(verticalHalfFov) * aspect);
                var halfFov = Mathf.Min(verticalHalfFov, horizontalHalfFov);
                distance = paddedRadius / Mathf.Max(0.01f, Mathf.Sin(halfFov));
            }

            camera.transform.SetPositionAndRotation(bounds.center - forward * distance, rotation);
            if (camera.farClipPlane < distance + radius)
                camera.farClipPlane = distance + radius * 2f;
            if (camera.nearClipPlane > distance - radius)
                camera.nearClipPlane = Mathf.Max(0.01f, (distance - radius) * 0.5f);
            camera.ResetProjectionMatrix();

            return new FrameResult(distance, orthographicSize);
        }

        private static Vector3 ProjectBounds(Bounds bounds, Quaternion cameraRotation)
        {
            Quaternion inverse = Quaternion.Inverse(cameraRotation);
            Vector3 extents = bounds.extents;
            var projected = Vector3.zero;
            for (int x = -1; x <= 1; x += 2)
            {
                for (int y = -1; y <= 1; y += 2)
                {
                    for (int z = -1; z <= 1; z += 2)
                    {
                        Vector3 corner = inverse * Vector3.Scale(extents, new Vector3(x, y, z));
                        projected.x = Mathf.Max(projected.x, Mathf.Abs(corner.x));
                        projected.y = Mathf.Max(projected.y, Mathf.Abs(corner.y));
                        projected.z = Mathf.Max(projected.z, Mathf.Abs(corner.z));
                    }
                }
            }
            return projected;
        }

        private static Bounds GetBounds(GameObject target, Renderer[] renderers)
        {
            var found = false;
            var bounds = default(Bounds);
            foreach (var renderer in renderers)
            {
                if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy)
                    continue;
                if (!found)
                {
                    bounds = renderer.bounds;
                    found = true;
                }
                else
                    bounds.Encapsulate(renderer.bounds);
            }

            if (!found)
                throw new McpToolException(McpErrorCodes.NotFound, $"Scene object '{SceneSerializer.GetHierarchyPath(target)}' has no enabled renderer in its hierarchy.");
            return bounds;
        }

        private static GameObject ResolveObject(Scene scene, JToken arguments)
        {
            var entityId = arguments["entityId"];
            if (entityId != null)
            {
                if (!UnitySerializer.TryParseEntityId(entityId, out EntityId parsed))
                    throw new McpToolException(McpErrorCodes.InvalidParams, "entityId must be an unsigned 64-bit integer string.");
                var obj = EditorUtility.EntityIdToObject(parsed);
                var go = obj as GameObject ?? (obj as Component)?.gameObject;
                if (go == null || go.scene != scene)
                    throw new McpToolException(McpErrorCodes.NotFound, $"GameObject not found for entityId {entityId} in scene '{scene.path}'.");
                return go;
            }

            var path = (string)arguments["path"];
            if (string.IsNullOrWhiteSpace(path))
                throw new McpToolException(McpErrorCodes.InvalidParams, "scene.preview_screenshot requires path or entityId.");

            foreach (var go in EnumerateSceneObjects(scene))
            {
                if (string.Equals(SceneSerializer.GetHierarchyPath(go), path, StringComparison.Ordinal))
                    return go;
            }
            throw new McpToolException(McpErrorCodes.NotFound, $"GameObject not found: {path}");
        }

        private static Camera ResolveCamera(Scene scene, string path)
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                foreach (var go in EnumerateSceneObjects(scene))
                {
                    if (string.Equals(SceneSerializer.GetHierarchyPath(go), path, StringComparison.Ordinal))
                    {
                        var camera = go.GetComponent<Camera>();
                        if (camera != null)
                            return camera;
                        throw new McpToolException(McpErrorCodes.NotFound, $"No Camera component found at cameraPath '{path}'.");
                    }
                }
                throw new McpToolException(McpErrorCodes.NotFound, $"Camera GameObject not found: {path}");
            }

            var main = Camera.main;
            if (main != null && main.enabled && main.gameObject.activeInHierarchy)
                return main;

            for (var sceneIndex = 0; sceneIndex < SceneManager.sceneCount; sceneIndex++)
            {
                foreach (var go in EnumerateSceneObjects(SceneManager.GetSceneAt(sceneIndex)))
                {
                    var camera = go.GetComponent<Camera>();
                    if (camera != null && camera.enabled && go.activeInHierarchy)
                        return camera;
                }
            }
            throw new McpToolException(McpErrorCodes.NotFound, "No enabled gameplay camera found in the loaded scenes.");
        }

        private static IEnumerable<GameObject> EnumerateSceneObjects(Scene scene)
        {
            foreach (var root in scene.GetRootGameObjects())
            {
                foreach (var go in EnumerateTree(root))
                    yield return go;
            }
        }

        private static IEnumerable<GameObject> EnumerateTree(GameObject root)
        {
            yield return root;
            foreach (Transform child in root.transform)
            {
                foreach (var go in EnumerateTree(child.gameObject))
                    yield return go;
            }
        }

        internal static int FindUnusedLayer(Scene targetScene)
        {
            var used = new bool[32];
            foreach (var go in EnumerateSceneObjects(targetScene))
                used[go.layer] = true;

            for (var sceneIndex = 0; sceneIndex < SceneManager.sceneCount; sceneIndex++)
            {
                var scene = SceneManager.GetSceneAt(sceneIndex);
                if (!scene.isLoaded || scene == targetScene)
                    continue;
                foreach (var go in EnumerateSceneObjects(scene))
                    used[go.layer] = true;
            }

            for (var layer = 31; layer >= 0; layer--)
            {
                if (!used[layer])
                    return layer;
            }
            throw new McpToolException(McpErrorCodes.ToolError, "No unused Unity layer is available for isolated preview capture.");
        }

        private static void SetLayerRecursive(GameObject root, int layer, List<LayerChange> changes)
        {
            changes.Add(new LayerChange(root, root.layer));
            root.layer = layer;
            foreach (Transform child in root.transform)
                SetLayerRecursive(child.gameObject, layer, changes);
        }

        private static void RestoreLayers(List<LayerChange> changes)
        {
            for (var i = changes.Count - 1; i >= 0; i--)
            {
                if (changes[i].Object != null)
                    changes[i].Object.layer = changes[i].Layer;
            }
        }

        private static int GetDefaultWidth(Camera camera) => camera.pixelWidth > 0 ? camera.pixelWidth : 1280;

        private static int GetDefaultHeight(Camera camera) => camera.pixelHeight > 0 ? camera.pixelHeight : 720;

        private readonly struct LayerChange
        {
            public GameObject Object { get; }
            public int Layer { get; }

            public LayerChange(GameObject @object, int layer)
            {
                Object = @object;
                Layer = layer;
            }
        }

        internal readonly struct FrameResult
        {
            public float Distance { get; }
            public float OrthographicSize { get; }

            public FrameResult(float distance, float orthographicSize)
            {
                Distance = distance;
                OrthographicSize = orthographicSize;
            }
        }
    }
}
