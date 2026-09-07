using System;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace LittleBrushGames.Mcp.Editor.Providers
{
    internal sealed class SceneAssetScope : IDisposable
    {
        private bool _disposed;
        private readonly Scene _previousActiveScene;

        private SceneAssetScope(
            Scene scene,
            string path,
            bool ownsPreviewScene,
            bool ownsTransientScene,
            bool created,
            Scene previousActiveScene = default)
        {
            Scene = scene;
            Path = path;
            OwnsPreviewScene = ownsPreviewScene;
            OwnsTransientScene = ownsTransientScene;
            Created = created;
            _previousActiveScene = previousActiveScene;
        }

        public Scene Scene { get; }
        public string Path { get; }
        public bool OwnsPreviewScene { get; }
        public bool OwnsTransientScene { get; }
        public bool Created { get; }

        public static SceneAssetScope Open(string rawPath)
        {
            if (string.IsNullOrWhiteSpace(rawPath))
                return FromActiveScene();

            var path = NormalizeScenePath(rawPath);
            var loaded = FindLoadedScene(path);
            if (loaded.IsValid())
                return new SceneAssetScope(loaded, path, false, false, false);
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(path) == null)
                throw new McpToolException(McpErrorCodes.NotFound, $"Scene asset not found: {path}");
            if (EditorApplication.isPlaying)
                throw new McpToolException(McpErrorCodes.ToolUnavailable,
                    "Reading an unloaded scene asset is unavailable in Play Mode.");

            var preview = EditorSceneManager.OpenPreviewScene(path);
            if (!preview.IsValid())
                throw new McpToolException(McpErrorCodes.ToolError, $"Failed to open scene asset in a preview scene: {path}");
            return new SceneAssetScope(preview, path, true, false, false);
        }

        public static SceneAssetScope OpenForWrite(string rawPath, bool createIfMissing)
        {
            if (string.IsNullOrWhiteSpace(rawPath))
            {
                if (createIfMissing)
                    throw new McpToolException(McpErrorCodes.ValidationFailed,
                        "createIfMissing requires an explicit scenePath.");
                return FromActiveScene();
            }

            var path = NormalizeScenePath(rawPath);
            var loaded = FindLoadedScene(path);
            if (loaded.IsValid())
                return new SceneAssetScope(loaded, path, false, false, false);
            if (EditorApplication.isPlaying)
                throw new McpToolException(McpErrorCodes.ToolUnavailable,
                    "Background scene asset writes are unavailable in Play Mode.");
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(path) != null)
                return OpenTransient(path, false);
            if (!createIfMissing)
                throw new McpToolException(McpErrorCodes.NotFound, $"Scene asset not found: {path}");

            ValidateWritablePath(path);
            return OpenTransient(path, true);
        }

        public void RequireExpectedHash(string expectedHash)
        {
            if (Created)
            {
                if (!string.IsNullOrWhiteSpace(expectedHash))
                    throw new McpToolException(McpErrorCodes.Conflict,
                        $"Scene does not exist but expectedHash was supplied: '{Path}'.");
                return;
            }

            if (string.IsNullOrWhiteSpace(expectedHash))
                throw new McpToolException(McpErrorCodes.ValidationFailed,
                    "expectedHash from scene.read is required when writing an existing scene asset.");

            if (!OwnsPreviewScene && !OwnsTransientScene && Scene.isDirty)
                throw new McpToolException(McpErrorCodes.Conflict,
                    $"Loaded scene has unsaved changes and cannot be written safely: '{Path}'.");

            var currentHash = GetAssetHash();
            if (!string.Equals(expectedHash, currentHash, StringComparison.Ordinal))
                throw new McpToolException(McpErrorCodes.Conflict,
                    $"Scene asset changed since it was read: '{Path}'.",
                    new JObject { ["expectedHash"] = expectedHash, ["currentHash"] = currentHash });
        }

        public void ValidateCanSave()
        {
            var path = string.IsNullOrWhiteSpace(Path) ? Scene.path : Path;
            if (string.IsNullOrWhiteSpace(path))
                throw new McpToolException(McpErrorCodes.ValidationFailed,
                    "An unsaved active scene requires an explicit scenePath.");
            ValidateWritablePath(path);
        }

        public void Save()
        {
            ValidateCanSave();
            if (!EditorSceneManager.SaveScene(Scene, Path))
                throw new McpToolException(McpErrorCodes.ToolError, $"Failed to save scene: '{Path}'.");
        }

        public JObject AddMetadata(JObject result)
        {
            result["scenePath"] = Path;
            result["sceneScope"] = OwnsPreviewScene ? "preview" : OwnsTransientScene ? "transient" : "loaded";
            result["created"] = Created;
            var hash = GetAssetHash();
            if (hash != null)
                result["assetHash"] = hash;
            return result;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            if (OwnsPreviewScene && Scene.IsValid() && !EditorSceneManager.ClosePreviewScene(Scene))
                Debug.LogError($"[MCP] Failed to close preview scene '{Path}'.");
            else if (OwnsTransientScene && Scene.IsValid() && !EditorSceneManager.CloseScene(Scene, true))
                Debug.LogError($"[MCP] Failed to close transient scene '{Path}'.");

            if (_previousActiveScene.IsValid() && _previousActiveScene.isLoaded &&
                SceneManager.GetActiveScene() != _previousActiveScene)
                SceneManager.SetActiveScene(_previousActiveScene);
        }

        private string GetAssetHash()
            => !string.IsNullOrWhiteSpace(Path) && AssetDatabase.LoadAssetAtPath<SceneAsset>(Path) != null
                ? AssetDatabase.GetAssetDependencyHash(Path).ToString()
                : null;

        private static SceneAssetScope FromActiveScene()
        {
            var active = SceneManager.GetActiveScene();
            if (!active.IsValid())
                throw new McpToolException(McpErrorCodes.NotFound, "No active scene loaded.");
            return new SceneAssetScope(active, active.path, false, false, false);
        }

        private static SceneAssetScope OpenTransient(string path, bool create)
        {
            var previousActive = SceneManager.GetActiveScene();
            Scene scene = default;
            try
            {
                scene = create
                    ? EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive)
                    : EditorSceneManager.OpenScene(path, OpenSceneMode.Additive);
                if (!scene.IsValid() || !scene.isLoaded)
                    throw new McpToolException(McpErrorCodes.ToolError,
                        $"Failed to open scene asset for background editing: {path}");

                if (SceneManager.GetActiveScene() != scene && !SceneManager.SetActiveScene(scene))
                    throw new McpToolException(McpErrorCodes.ToolError,
                        "Failed to activate the transient background scene.");

                return new SceneAssetScope(scene, path, false, true, create, previousActive);
            }
            catch
            {
                if (scene.IsValid() && scene.isLoaded)
                    EditorSceneManager.CloseScene(scene, true);
                throw;
            }
        }

        private static Scene FindLoadedScene(string path)
        {
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var loaded = SceneManager.GetSceneAt(i);
                if (loaded.isLoaded && string.Equals(loaded.path, path, StringComparison.OrdinalIgnoreCase))
                    return loaded;
            }
            return default;
        }

        private static string NormalizeScenePath(string rawPath)
        {
            var path = AssetProvider.NormalizeAssetPath(rawPath);
            if (!path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
                throw new McpToolException(McpErrorCodes.ValidationFailed,
                    $"scenePath must end with '.unity', got '{rawPath}'.");
            return path;
        }

        private static void ValidateWritablePath(string path)
        {
            if (!path.StartsWith("Assets/", StringComparison.Ordinal))
                throw new McpToolException(McpErrorCodes.ValidationFailed,
                    $"Writable scenes must be under Assets/, got '{path}'.");

            var directory = System.IO.Path.GetDirectoryName(path)?.Replace('\\', '/');
            if (string.IsNullOrWhiteSpace(directory) || !AssetDatabase.IsValidFolder(directory))
                throw new McpToolException(McpErrorCodes.ValidationFailed,
                    $"Scene folder does not exist: '{directory}'.");
        }
    }
}
