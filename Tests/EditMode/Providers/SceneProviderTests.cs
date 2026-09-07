using System.Collections.Generic;
using System.Linq;
using System.Threading;
using LittleBrushGames.Mcp.Editor.Host;
using LittleBrushGames.Mcp.Editor.Providers;
using LittleBrushGames.Mcp.Tests.Fakes;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace LittleBrushGames.Mcp.Tests.Providers
{
    public class SceneProviderTests
    {
        private string _baseScenePath;
        private string _runnerSceneName;

        [SetUp]
        public void SaveOwnedRunnerSceneForAdditiveFixtures()
        {
            var active = SceneManager.GetActiveScene();
            // The framework may rename the disposable scene; verify our active run ownership.
            // Never save or replace an arbitrary untitled user scene.
            if (!active.IsValid() || !string.IsNullOrEmpty(active.path)) return;
            if (!Application.isBatchMode && string.IsNullOrEmpty(SessionState.GetString("McpTestRun_ActiveRunIds", ""))) return;
            _runnerSceneName = active.name;
            _baseScenePath = $"Assets/__McpSceneProviderBase_{System.Guid.NewGuid():N}.unity";
            Assert.That(EditorSceneManager.SaveScene(active, _baseScenePath), Is.True);
        }

        [TearDown]
        public void RemoveOwnedRunnerSceneFixture()
        {
            if (_baseScenePath == null) return;
            var scene = SceneManager.GetSceneByPath(_baseScenePath);
            if (scene.IsValid() && scene.isLoaded)
            {
                if (SceneManager.sceneCount == 1)
                {
                    var empty = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                    empty.name = _runnerSceneName;
                }
                else EditorSceneManager.CloseScene(scene, true);
            }
            AssetDatabase.DeleteAsset(_baseScenePath);
            _baseScenePath = null;
        }

        [Test]
        public void RegisterTools_DeclaresSceneQueryTools()
        {
            var names = CollectFor(new SceneProvider()).Tools.Select(t => t.Name).ToList();
            Assert.That(names, Contains.Item("scene.request_open"));
            Assert.That(names, Does.Not.Contain("scene.open"));
            Assert.That(names, Contains.Item("scene.find"));
            Assert.That(names, Contains.Item("scene.read_object"));
            Assert.That(names, Contains.Item("scene.replace_id"));
            Assert.That(names, Does.Not.Contain("scene.create"));

            var write = CollectFor(new SceneProvider()).Tools.First(t => t.Name == "scene.write");
            Assert.That(write.InputSchema["properties"]["createIfMissing"], Is.Not.Null);
            Assert.That(write.InputSchema["properties"]["expectedHash"], Is.Not.Null);
        }

        [Test]
        public void RequestOpenScene_RequiresReasonAndOnlySupportsSingle()
        {
            var requestOpen = CollectFor(new SceneProvider()).Tools.First(t => t.Name == "scene.request_open");
            var required = requestOpen.InputSchema["required"].Values<string>().ToList();

            Assert.That(required, Does.Contain("path"));
            Assert.That(required, Does.Contain("reason"));
            Assert.That(requestOpen.InputSchema["properties"]["mode"], Is.Null);
            Assert.That(requestOpen.Execution, Is.EqualTo(ToolExecution.Async));
            Assert.That(requestOpen.Timeout, Is.EqualTo(System.TimeSpan.FromMinutes(5) + System.TimeSpan.FromSeconds(10)));
        }

        [Test]
        public void RequestOpenScene_RejectsAdditiveMode()
        {
            var requestOpen = CollectFor(new SceneProvider()).Tools.First(t => t.Name == "scene.request_open");
            var error = Assert.Throws<McpToolException>(() => requestOpen.Handler(Ctx(new JObject
            {
                ["path"] = "Assets/AnyScene.unity",
                ["mode"] = "additive",
                ["reason"] = "inspect another scene",
            }), CancellationToken.None).AsTask().GetAwaiter().GetResult());

            Assert.That(error.Code, Is.EqualTo(McpErrorCodes.InvalidParams));
            Assert.That(error.Message, Is.EqualTo("Only single-scene opening is supported."));
        }

        [Test]
        public void RequestOpenScene_SingleHonorsUnsavedWorkDenyPolicy()
        {
            var targetPath = $"Assets/__McpSceneTarget_{System.Guid.NewGuid():N}.unity";
            var dirty = default(Scene);
            var hadOverride = McpTrustPolicy.TryGetOverride(ToolTrustCategory.UnsavedWork, out var originalOverride);
            McpTrustPolicy.SetOverride(ToolTrustCategory.UnsavedWork, McpTrustDecision.Deny);
            try
            {
                CreateSavedTestScene(targetPath);
                dirty = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
                EditorSceneManager.MarkSceneDirty(dirty);

                var requestOpen = CollectFor(new SceneProvider()).Tools.First(t => t.Name == "scene.request_open");
                var loadedSceneCount = SceneManager.sceneCount;
                var error = Assert.Throws<McpToolException>(() => requestOpen.Handler(Ctx(new JObject
                {
                    ["path"] = targetPath,
                    ["reason"] = "switch to the target scene",
                }), CancellationToken.None).AsTask().GetAwaiter().GetResult());

                Assert.That(error.Code, Is.EqualTo(McpErrorCodes.ToolUnavailable));
                Assert.That((string)error.Data["trustCategory"], Is.EqualTo(ToolTrustCategory.UnsavedWork.ToString()));
                Assert.That(SceneManager.sceneCount, Is.EqualTo(loadedSceneCount));
                Assert.That(SceneManager.GetSceneByPath(targetPath).isLoaded, Is.True);
                Assert.That(dirty.isLoaded, Is.True);
            }
            finally
            {
                if (hadOverride) McpTrustPolicy.SetOverride(ToolTrustCategory.UnsavedWork, originalOverride);
                else McpTrustPolicy.ClearOverride(ToolTrustCategory.UnsavedWork);
                if (dirty.IsValid())
                    EditorSceneManager.CloseScene(dirty, true);
                ResetTestScene(targetPath);
            }
        }

        [Test]
        public void FindAndReadObject_ReturnsTargetedSceneData()
        {
            GameObject root = null;
            try
            {
                root = new GameObject("McpSceneProbeRoot");
                var child = new GameObject("McpSceneProbeChild");
                child.transform.SetParent(root.transform);
                child.AddComponent<BoxCollider>();
                child.SetActive(false);

                var tools = CollectFor(new SceneProvider()).Tools;
                var find = tools.First(t => t.Name == "scene.find");
                var readObject = tools.First(t => t.Name == "scene.read_object");

                var findArgs = new JObject
                {
                    ["name"] = "McpSceneProbeChild",
                    ["componentType"] = "BoxCollider",
                    ["includeInactive"] = true,
                    ["includeComponents"] = true,
                };
                var findResult = find.Handler(Ctx(findArgs), CancellationToken.None).AsTask().GetAwaiter().GetResult();
                var matches = (JArray)findResult.StructuredContent["matches"];
                Assert.That(matches.Select(m => (string)m["path"]), Contains.Item("McpSceneProbeRoot/McpSceneProbeChild"));
                Assert.That(matches[0]["components"], Is.Not.Null);

                var readArgs = new JObject
                {
                    ["path"] = "McpSceneProbeRoot/McpSceneProbeChild",
                    ["includeComponents"] = true,
                };
                var readResult = readObject.Handler(Ctx(readArgs), CancellationToken.None).AsTask().GetAwaiter().GetResult();
                Assert.That((string)readResult.StructuredContent["object"]["name"], Is.EqualTo("McpSceneProbeChild"));
                Assert.That(readResult.StructuredContent["object"]["components"], Is.Not.Null);
            }
            finally
            {
                if (root != null)
                    Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void Read_DefaultsToHierarchyWithoutComponents()
        {
            GameObject root = null;
            try
            {
                root = new GameObject("McpCompactSceneRoot");
                root.AddComponent<BoxCollider>();
                var read = CollectFor(new SceneProvider()).Tools.First(t => t.Name == "scene.read");

                // maxNodes is explicit because the assertions below are about the OUTLINE profile
                // (no components, no path, no localPosition) and not about the node budget. With the
                // budget left at its default this test silently depends on the active scene being
                // nearly empty: roots are serialized in Ordinal name order, so in a scene that a
                // previous test (or a consumer project's own suite) left populated, the budget is
                // spent before the walk reaches "McpCompactSceneRoot" and Single() throws
                // "Sequence contains no matching element" — a failure that says nothing about the
                // behaviour under test.
                var result = read.Handler(Ctx(new JObject { ["maxDepth"] = 1, ["maxNodes"] = 5000 }), CancellationToken.None)
                    .AsTask().GetAwaiter().GetResult();

                // Guard, so a budget that is somehow still too small fails as itself rather than as
                // a missing element.
                Assert.That((bool)result.StructuredContent["truncated"], Is.False,
                    "the scene walk was truncated, so the probe object may be absent for a reason unrelated to this test");

                var serialized = ((JArray)result.StructuredContent["roots"])
                    .OfType<JObject>().Single(item => (string)item["name"] == root.name);

                Assert.That(serialized["components"], Is.Null);
                Assert.That(serialized["path"], Is.Null);
                Assert.That(serialized["localPosition"], Is.Null);
            }
            finally
            {
                if (root != null)
                    Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void ReadObject_PropertiesAreTargetedAndPaged()
        {
            GameObject root = null;
            try
            {
                root = new GameObject("McpPropertyProbe");
                root.AddComponent<BoxCollider>();
                var read = CollectFor(new SceneProvider()).Tools.First(t => t.Name == "scene.read_object");
                var result = read.Handler(Ctx(new JObject
                {
                    ["path"] = root.name,
                    ["includeProperties"] = true,
                    ["componentTypes"] = new JArray("BoxCollider"),
                    ["propertySearch"] = "Center",
                    ["propertyLimit"] = 1,
                }), CancellationToken.None).AsTask().GetAwaiter().GetResult();

                var components = (JArray)result.StructuredContent["object"]["components"];
                Assert.That(components.Count, Is.EqualTo(1));
                Assert.That((string)components[0]["type"], Is.EqualTo(typeof(BoxCollider).FullName));
                Assert.That((int)components[0]["propertyReturned"], Is.LessThanOrEqualTo(1));
            }
            finally
            {
                if (root != null)
                    Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void Find_DefaultPageIsBoundedAndSupportsOffset()
        {
            var roots = new List<GameObject>();
            try
            {
                for (var i = 0; i < 30; i++)
                    roots.Add(new GameObject($"McpPagedFind{i:00}"));
                var find = CollectFor(new SceneProvider()).Tools.First(t => t.Name == "scene.find");

                var first = find.Handler(Ctx(new JObject { ["name"] = "McpPagedFind" }), CancellationToken.None)
                    .AsTask().GetAwaiter().GetResult().StructuredContent;
                Assert.That((int)first["count"], Is.EqualTo(30));
                Assert.That((int)first["returned"], Is.EqualTo(25));
                Assert.That((int)first["nextOffset"], Is.EqualTo(25));

                var second = find.Handler(Ctx(new JObject { ["name"] = "McpPagedFind", ["offset"] = 25 }), CancellationToken.None)
                    .AsTask().GetAwaiter().GetResult().StructuredContent;
                Assert.That((int)second["returned"], Is.EqualTo(5));
                Assert.That((bool)second["truncated"], Is.False);
            }
            finally
            {
                foreach (var root in roots)
                    Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void ScenePreview_UnusedLayerIncludesPreviewSceneObjects()
        {
            var preview = EditorSceneManager.NewPreviewScene();
            try
            {
                for (var layer = 0; layer < 32; layer++)
                {
                    var root = new GameObject($"PreviewLayer{layer}") { layer = layer };
                    SceneManager.MoveGameObjectToScene(root, preview);
                }

                var error = Assert.Throws<McpToolException>(() => ScenePreviewProvider.FindUnusedLayer(preview));

                Assert.That(error.Code, Is.EqualTo(McpErrorCodes.ToolError));
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(preview);
            }
        }

        [Test]
        public void Read_UnloadedSceneUsesPreviewWithoutChangingLoadedSetup()
        {
            var path = $"Assets/__McpBackgroundRead_{System.Guid.NewGuid():N}.unity";
            CreateSceneAsset(path, "BackgroundRoot");
            var active = SceneManager.GetActiveScene();
            var activeWasDirty = active.isDirty;
            var loadedCount = SceneManager.sceneCount;
            var previewCount = EditorSceneManager.previewSceneCount;
            try
            {
                var read = CollectFor(new SceneProvider()).Tools.First(t => t.Name == "scene.read");
                var result = read.Handler(Ctx(new JObject
                {
                    ["scenePath"] = path,
                    ["maxDepth"] = 1,
                }), CancellationToken.None).AsTask().GetAwaiter().GetResult().StructuredContent;

                Assert.That((string)result["sceneScope"], Is.EqualTo("preview"));
                Assert.That((string)result["assetHash"], Is.Not.Empty);
                Assert.That(((JArray)result["roots"]).Values<JObject>().Select(root => (string)root["name"]),
                    Does.Contain("BackgroundRoot"));
                AssertLoadedSetupUnchanged(active, activeWasDirty, loadedCount, previewCount);
            }
            finally
            {
                ResetTestScene(path);
            }
        }

        [Test]
        public void Write_CreateIfMissingPersistsSceneWithoutChangingLoadedSetup()
        {
            var path = $"Assets/__McpBackgroundCreate_{System.Guid.NewGuid():N}.unity";
            var active = SceneManager.GetActiveScene();
            var activeWasDirty = active.isDirty;
            var loadedCount = SceneManager.sceneCount;
            var previewCount = EditorSceneManager.previewSceneCount;
            try
            {
                var write = CollectFor(new SceneProvider()).Tools.First(t => t.Name == "scene.write");
                var result = write.Handler(Ctx(new JObject
                {
                    ["scenePath"] = path,
                    ["createIfMissing"] = true,
                    ["save"] = true,
                    ["operations"] = new JArray
                    {
                        new JObject
                        {
                            ["type"] = "create_gameobject",
                            ["name"] = "CreatedRoot",
                            ["localPosition"] = new JObject { ["x"] = 1, ["y"] = 2, ["z"] = 3 },
                        },
                        new JObject
                        {
                            ["type"] = "add_component",
                            ["path"] = "CreatedRoot",
                            ["componentType"] = "UnityEngine.BoxCollider",
                        },
                    },
                }), CancellationToken.None).AsTask().GetAwaiter().GetResult().StructuredContent;

                Assert.That((bool)result["created"], Is.True);
                Assert.That((bool)result["saved"], Is.True);
                Assert.That((string)result["sceneScope"], Is.EqualTo("transient"));
                Assert.That((string)result["assetHash"], Is.Not.Empty);
                AssertLoadedSetupUnchanged(active, activeWasDirty, loadedCount, previewCount);

                var preview = EditorSceneManager.OpenPreviewScene(path);
                try
                {
                    var root = preview.GetRootGameObjects().Single(go => go.name == "CreatedRoot");
                    Assert.That(root.transform.localPosition, Is.EqualTo(new Vector3(1, 2, 3)));
                    Assert.That(root.GetComponent<BoxCollider>(), Is.Not.Null);
                }
                finally
                {
                    EditorSceneManager.ClosePreviewScene(preview);
                }
            }
            finally
            {
                ResetTestScene(path);
            }
        }

        [Test]
        public void Write_UnloadedSceneRejectsStaleHashWithoutOverwritingFirstWriter()
        {
            var path = $"Assets/__McpBackgroundConflict_{System.Guid.NewGuid():N}.unity";
            CreateSceneAsset(path, "Root");
            try
            {
                var tools = CollectFor(new SceneProvider()).Tools;
                var read = tools.First(t => t.Name == "scene.read");
                var write = tools.First(t => t.Name == "scene.write");
                var hash = (string)read.Handler(Ctx(new JObject { ["scenePath"] = path }), CancellationToken.None)
                    .AsTask().GetAwaiter().GetResult().StructuredContent["assetHash"];

                write.Handler(Ctx(new JObject
                {
                    ["scenePath"] = path,
                    ["expectedHash"] = hash,
                    ["save"] = true,
                    ["operations"] = new JArray
                    {
                        new JObject { ["type"] = "rename_gameobject", ["path"] = "Root", ["newName"] = "FirstWriter" },
                    },
                }), CancellationToken.None).AsTask().GetAwaiter().GetResult();

                var error = Assert.Throws<McpToolException>(() => write.Handler(Ctx(new JObject
                {
                    ["scenePath"] = path,
                    ["expectedHash"] = hash,
                    ["save"] = true,
                    ["operations"] = new JArray
                    {
                        new JObject { ["type"] = "rename_gameobject", ["path"] = "Root", ["newName"] = "SecondWriter" },
                    },
                }), CancellationToken.None).AsTask().GetAwaiter().GetResult());

                Assert.That(error.Code, Is.EqualTo(McpErrorCodes.Conflict));
                var preview = EditorSceneManager.OpenPreviewScene(path);
                try
                {
                    Assert.That(preview.GetRootGameObjects().Single().name, Is.EqualTo("FirstWriter"));
                }
                finally
                {
                    EditorSceneManager.ClosePreviewScene(preview);
                }
            }
            finally
            {
                ResetTestScene(path);
            }
        }

        [Test]
        public void Write_UnloadedSceneRequiresExplicitSaveAndCurrentHash()
        {
            var path = $"Assets/__McpBackgroundGuard_{System.Guid.NewGuid():N}.unity";
            CreateSceneAsset(path, "Root");
            var previewCount = EditorSceneManager.previewSceneCount;
            try
            {
                var tools = CollectFor(new SceneProvider()).Tools;
                var write = tools.First(t => t.Name == "scene.write");
                var hash = (string)tools.First(t => t.Name == "scene.read")
                    .Handler(Ctx(new JObject { ["scenePath"] = path }), CancellationToken.None)
                    .AsTask().GetAwaiter().GetResult().StructuredContent["assetHash"];
                var operations = new JArray
                {
                    new JObject { ["type"] = "rename_gameobject", ["path"] = "Root", ["newName"] = "Changed" },
                };

                var missingSave = Assert.Throws<McpToolException>(() => write.Handler(Ctx(new JObject
                {
                    ["scenePath"] = path,
                    ["expectedHash"] = hash,
                    ["operations"] = operations,
                }), CancellationToken.None).AsTask().GetAwaiter().GetResult());
                Assert.That(missingSave.Code, Is.EqualTo(McpErrorCodes.ValidationFailed));

                var missingHash = Assert.Throws<McpToolException>(() => write.Handler(Ctx(new JObject
                {
                    ["scenePath"] = path,
                    ["save"] = true,
                    ["operations"] = operations,
                }), CancellationToken.None).AsTask().GetAwaiter().GetResult());
                Assert.That(missingHash.Code, Is.EqualTo(McpErrorCodes.ValidationFailed));
                Assert.That(EditorSceneManager.previewSceneCount, Is.EqualTo(previewCount));

                var preview = EditorSceneManager.OpenPreviewScene(path);
                try
                {
                    Assert.That(preview.GetRootGameObjects().Single().name, Is.EqualTo("Root"));
                }
                finally
                {
                    EditorSceneManager.ClosePreviewScene(preview);
                }
            }
            finally
            {
                ResetTestScene(path);
            }
        }

        [Test]
        public void Write_LoadedSceneRequiresCurrentHashAndRejectsUnsavedWork()
        {
            var path = $"Assets/__McpLoadedGuard_{System.Guid.NewGuid():N}.unity";
            var previousActive = SceneManager.GetActiveScene();
            var loaded = previousActive.IsValid() && string.IsNullOrEmpty(previousActive.path)
                ? previousActive
                : EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            new GameObject("Root");
            Assert.That(EditorSceneManager.SaveScene(loaded, path), Is.True);
            try
            {
                var tools = CollectFor(new SceneProvider()).Tools;
                var write = tools.First(t => t.Name == "scene.write");
                var hash = (string)tools.First(t => t.Name == "scene.read")
                    .Handler(Ctx(new JObject { ["scenePath"] = path }), CancellationToken.None)
                    .AsTask().GetAwaiter().GetResult().StructuredContent["assetHash"];
                var operations = new JArray
                {
                    new JObject { ["type"] = "rename_gameobject", ["path"] = "Root", ["newName"] = "Changed" },
                };

                var missingHash = Assert.Throws<McpToolException>(() => write.Handler(Ctx(new JObject
                {
                    ["scenePath"] = path,
                    ["save"] = true,
                    ["operations"] = operations,
                }), CancellationToken.None).AsTask().GetAwaiter().GetResult());
                Assert.That(missingHash.Code, Is.EqualTo(McpErrorCodes.ValidationFailed));

                var unsaved = new GameObject("UnsavedUserWork");
                SceneManager.MoveGameObjectToScene(unsaved, loaded);
                EditorSceneManager.MarkSceneDirty(loaded);
                var dirtyScene = Assert.Throws<McpToolException>(() => write.Handler(Ctx(new JObject
                {
                    ["scenePath"] = path,
                    ["expectedHash"] = hash,
                    ["save"] = true,
                    ["operations"] = operations,
                }), CancellationToken.None).AsTask().GetAwaiter().GetResult());

                Assert.That(dirtyScene.Code, Is.EqualTo(McpErrorCodes.Conflict));
                Assert.That(loaded.GetRootGameObjects().Select(go => go.name), Does.Contain("Root"));
                Assert.That(loaded.GetRootGameObjects().Select(go => go.name), Does.Contain("UnsavedUserWork"));
            }
            finally
            {
                ResetTestScene(path);
                if (previousActive != loaded && previousActive.IsValid() && previousActive.isLoaded)
                    SceneManager.SetActiveScene(previousActive);
            }
        }

        [Test]
        public void ReplaceId_UnloadedScenePersistsWithHashWithoutChangingLoadedSetup()
        {
            var path = $"Assets/__McpBackgroundReplace_{System.Guid.NewGuid():N}.unity";
            CreateSceneAsset(path, "Root",
                root => root.AddComponent<SceneSerializedReplacementTestComponent>().id = "old.id");
            var setup = SceneManager.GetActiveScene();
            var setupWasDirty = setup.isDirty;
            var loadedCount = SceneManager.sceneCount;
            var previewCount = EditorSceneManager.previewSceneCount;

            try
            {
                var tools = CollectFor(new SceneProvider()).Tools;
                var hash = (string)tools.First(t => t.Name == "scene.read")
                    .Handler(Ctx(new JObject { ["scenePath"] = path }), CancellationToken.None)
                    .AsTask().GetAwaiter().GetResult().StructuredContent["assetHash"];
                var result = tools.First(t => t.Name == "scene.replace_id")
                    .Handler(Ctx(new JObject
                    {
                        ["scenePath"] = path,
                        ["expectedHash"] = hash,
                        ["oldId"] = "old.id",
                        ["newId"] = "new.id",
                        ["save"] = true,
                    }), CancellationToken.None).AsTask().GetAwaiter().GetResult().StructuredContent;

                Assert.That((bool)result["saved"], Is.True);
                Assert.That((int)result["changedCount"], Is.EqualTo(1));
                Assert.That((string)result["assetHash"], Is.Not.EqualTo(hash));
                AssertLoadedSetupUnchanged(setup, setupWasDirty, loadedCount, previewCount);

                var verify = EditorSceneManager.OpenPreviewScene(path);
                try
                {
                    Assert.That(verify.GetRootGameObjects().Single()
                        .GetComponent<SceneSerializedReplacementTestComponent>().id, Is.EqualTo("new.id"));
                }
                finally
                {
                    EditorSceneManager.ClosePreviewScene(verify);
                }
            }
            finally
            {
                ResetTestScene(path);
            }
        }

        [Test]
        public void Write_FailedBackgroundBatchDoesNotPersistOrLeakTransientScene()
        {
            var path = $"Assets/__McpBackgroundRollback_{System.Guid.NewGuid():N}.unity";
            CreateSceneAsset(path, "Root");
            var active = SceneManager.GetActiveScene();
            var activeWasDirty = active.isDirty;
            var loadedCount = SceneManager.sceneCount;
            var previewCount = EditorSceneManager.previewSceneCount;
            try
            {
                var tools = CollectFor(new SceneProvider()).Tools;
                var hash = (string)tools.First(t => t.Name == "scene.read")
                    .Handler(Ctx(new JObject { ["scenePath"] = path }), CancellationToken.None)
                    .AsTask().GetAwaiter().GetResult().StructuredContent["assetHash"];
                var result = tools.First(t => t.Name == "scene.write")
                    .Handler(Ctx(new JObject
                    {
                        ["scenePath"] = path,
                        ["expectedHash"] = hash,
                        ["save"] = true,
                        ["operations"] = new JArray
                        {
                            new JObject { ["type"] = "create_gameobject", ["name"] = "MustRollback" },
                            new JObject { ["type"] = "unsupported_operation" },
                        },
                    }), CancellationToken.None).AsTask().GetAwaiter().GetResult().StructuredContent;

                Assert.That((bool)result["rolledBack"], Is.True);
                Assert.That((bool)result["saved"], Is.False);
                AssertLoadedSetupUnchanged(active, activeWasDirty, loadedCount, previewCount);

                var preview = EditorSceneManager.OpenPreviewScene(path);
                try
                {
                    Assert.That(preview.GetRootGameObjects().Select(go => go.name), Does.Not.Contain("MustRollback"));
                }
                finally
                {
                    EditorSceneManager.ClosePreviewScene(preview);
                }
            }
            finally
            {
                ResetTestScene(path);
            }
        }

        private static CollectingSink CollectFor(IToolProvider provider)
        {
            var sink = new CollectingSink();
            provider.RegisterTools(sink);
            return sink;
        }

        private static ToolContext Ctx(JObject args) => new(
            args,
            new NoopProgressReporter(),
            new FakeMainThreadPump(),
            new FakeFrameWaiter(),
            null,
            new FakeLogSink(),
            "req");

        private static void CreateSavedTestScene(string path)
        {
            Scene active = SceneManager.GetActiveScene();
            Scene scene;
            if (active.IsValid() && string.IsNullOrEmpty(active.path))
            {
                scene = active;
            }
            else
            {
                scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            }
            Assert.That(EditorSceneManager.SaveScene(scene, path), Is.True);
        }

        private static void CreateSceneAsset(
            string path,
            string rootName,
            System.Action<GameObject> configure = null)
        {
            var previousActive = SceneManager.GetActiveScene();
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            try
            {
                var root = new GameObject(rootName);
                configure?.Invoke(root);
                Assert.That(EditorSceneManager.SaveScene(scene, path), Is.True);
            }
            finally
            {
                EditorSceneManager.CloseScene(scene, true);
                if (previousActive.IsValid() && previousActive.isLoaded &&
                    SceneManager.GetActiveScene() != previousActive)
                    SceneManager.SetActiveScene(previousActive);
            }
        }

        private static void AssertLoadedSetupUnchanged(
            Scene active,
            bool activeWasDirty,
            int loadedCount,
            int previewCount)
        {
            Assert.That(SceneManager.sceneCount, Is.EqualTo(loadedCount));
            Assert.That(SceneManager.GetActiveScene(), Is.EqualTo(active));
            Assert.That(active.isDirty, Is.EqualTo(activeWasDirty));
            Assert.That(EditorSceneManager.previewSceneCount, Is.EqualTo(previewCount));
        }

        private static void ResetTestScene(string path)
        {
            Scene scene = SceneManager.GetSceneByPath(path);
            if (scene.IsValid() && scene.isLoaded)
            {
                if (SceneManager.sceneCount == 1)
                    EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                else
                    EditorSceneManager.CloseScene(scene, true);
            }
            AssetDatabase.DeleteAsset(path);
        }

        private sealed class CollectingSink : IToolRegistration
        {
            public List<ToolDescriptor> Tools = new();
            public void Register(ToolDescriptor descriptor) => Tools.Add(descriptor);
        }
    }
}
