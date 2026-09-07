using System.Collections.Generic;
using System.Linq;
using System.Threading;
using LittleBrushGames.Mcp.Editor.Providers;
using LittleBrushGames.Mcp.Tests.Fakes;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace LittleBrushGames.Mcp.Tests.Providers
{
    public class PrefabProviderTests
    {
        [Test]
        public void RegisterTools_DeclaresAllPrefabTools()
        {
            var sink = Collect(new PrefabProvider());
            var names = sink.Tools.Select(t => t.Name).ToList();
            Assert.That(names, Contains.Item("prefab.create"));
            Assert.That(names, Contains.Item("prefab.create_from_scene_object"));
            Assert.That(names, Contains.Item("prefab.read"));
            Assert.That(names, Contains.Item("prefab.add_nested_prefab"));
            Assert.That(names, Contains.Item("prefab.write"));
            Assert.That(names, Contains.Item("prefab.replace_id"));
            var cleanup = sink.Tools.Single(t => t.Name == "prefab.remove_unused_overrides");
            Assert.That(cleanup.Availability, Is.EqualTo(ToolAvailability.EditMode));
            Assert.That(cleanup.InputSchema["properties"]["dryRun"], Is.Not.Null);
            Assert.That(cleanup.InputSchema["properties"]["reportLimit"]["maximum"].Value<int>(), Is.EqualTo(256));
            var preview = sink.Tools.Single(t => t.Name == "prefab.preview_screenshot");
            Assert.That((preview.Availability & ToolAvailability.PlayMode) != 0, Is.True);
            Assert.That(preview.InputSchema["properties"]["cameraPath"], Is.Not.Null);
            Assert.That(preview.InputSchema["properties"]["rotation"], Is.Not.Null);
            Assert.That(preview.InputSchema["properties"]["framePadding"], Is.Not.Null);
            Assert.That(preview.Description, Does.Contain("3D prefab"));
            var createFromScene = sink.Tools.Single(t => t.Name == "prefab.create_from_scene_object");
            Assert.That(createFromScene.Availability, Is.EqualTo(ToolAvailability.EditMode));
            Assert.That(createFromScene.InputSchema["properties"]["dryRun"], Is.Not.Null);
            Assert.That(createFromScene.InputSchema["properties"]["confirm"], Is.Not.Null);
            Assert.That(createFromScene.InputSchema["properties"]["excludePaths"], Is.Not.Null);
        }

        [Test]
        public void RemoveUnusedOverrides_DryRunPreservesValidNestedPrefabOverride()
        {
            const string folder = "Assets/__prefab_override_cleanup_test__";
            const string sourcePath = folder + "/Source.prefab";
            const string destinationPath = folder + "/Destination.prefab";
            if (!UnityEditor.AssetDatabase.IsValidFolder(folder))
                UnityEditor.AssetDatabase.CreateFolder("Assets", "__prefab_override_cleanup_test__");

            var source = new UnityEngine.GameObject("Source");
            UnityEditor.PrefabUtility.SaveAsPrefabAsset(source, sourcePath);
            UnityEngine.Object.DestroyImmediate(source);
            var destination = new UnityEngine.GameObject("Destination");
            UnityEditor.PrefabUtility.SaveAsPrefabAsset(destination, destinationPath);
            UnityEngine.Object.DestroyImmediate(destination);

            try
            {
                var contents = UnityEditor.PrefabUtility.LoadPrefabContents(destinationPath);
                try
                {
                    var sourceAsset = UnityEditor.AssetDatabase.LoadAssetAtPath<UnityEngine.GameObject>(sourcePath);
                    var nested = (UnityEngine.GameObject)UnityEditor.PrefabUtility.InstantiatePrefab(sourceAsset, contents.scene);
                    nested.transform.SetParent(contents.transform, false);
                    nested.transform.localPosition = UnityEngine.Vector3.one;
                    UnityEditor.PrefabUtility.RecordPrefabInstancePropertyModifications(nested.transform);
                    var second = (UnityEngine.GameObject)UnityEditor.PrefabUtility.InstantiatePrefab(sourceAsset, contents.scene);
                    second.transform.SetParent(contents.transform, false);
                    second.transform.localPosition = UnityEngine.Vector3.right * 2f;
                    UnityEditor.PrefabUtility.RecordPrefabInstancePropertyModifications(second.transform);
                    UnityEditor.PrefabUtility.SaveAsPrefabAsset(contents, destinationPath, out var success);
                    Assert.That(success, Is.True);
                }
                finally { UnityEditor.PrefabUtility.UnloadPrefabContents(contents); }

                var cleanup = Collect(new PrefabProvider()).Tools.Single(t => t.Name == "prefab.remove_unused_overrides");
                var result = cleanup.Handler(Ctx(new JObject
                {
                    ["path"] = destinationPath,
                    ["dryRun"] = true,
                    ["reportLimit"] = 1,
                }), CancellationToken.None).AsTask().GetAwaiter().GetResult();

                Assert.That((bool)result.StructuredContent["dryRun"], Is.True);
                Assert.That((bool)result.StructuredContent["saved"], Is.False);
                Assert.That((int)result.StructuredContent["instanceCount"], Is.EqualTo(2));
                Assert.That((int)result.StructuredContent["removedUnusedPropertyOverrideCount"], Is.EqualTo(0));
                Assert.That((int)result.StructuredContent["propertyOverrideCountAfter"],
                    Is.EqualTo((int)result.StructuredContent["propertyOverrideCountBefore"]));
                Assert.That(((JArray)result.StructuredContent["instances"]).Count, Is.EqualTo(1));
                Assert.That((bool)result.StructuredContent["reportTruncated"], Is.True);
                Assert.That((int)result.StructuredContent["nextReportOffset"], Is.EqualTo(1));

                // Dry-run cleanup is discarded with the isolated prefab scene. Re-open the
                // asset and verify the valid nested transform override is still authored.
                var readback = UnityEditor.PrefabUtility.LoadPrefabContents(destinationPath);
                try
                {
                    var nested = readback.transform.Find("Source");
                    Assert.That(nested, Is.Not.Null);
                    Assert.That(nested.localPosition, Is.EqualTo(UnityEngine.Vector3.one));
                }
                finally { UnityEditor.PrefabUtility.UnloadPrefabContents(readback); }
            }
            finally
            {
                UnityEditor.AssetDatabase.DeleteAsset(folder);
            }
        }

        [Test]
        public void RemoveUnusedOverrides_AppliesStaleNestedPropertyOverrideAndReadsBack()
        {
            const string folder = "Assets/__prefab_stale_override_cleanup_test__";
            const string sourcePath = folder + "/Source.prefab";
            const string destinationPath = folder + "/Destination.prefab";
            if (!UnityEditor.AssetDatabase.IsValidFolder(folder))
                UnityEditor.AssetDatabase.CreateFolder("Assets", "__prefab_stale_override_cleanup_test__");

            var source = new UnityEngine.GameObject("Source");
            source.AddComponent<UnityEngine.BoxCollider>();
            UnityEditor.PrefabUtility.SaveAsPrefabAsset(source, sourcePath);
            UnityEngine.Object.DestroyImmediate(source);
            var destination = new UnityEngine.GameObject("Destination");
            UnityEditor.PrefabUtility.SaveAsPrefabAsset(destination, destinationPath);
            UnityEngine.Object.DestroyImmediate(destination);

            try
            {
                var destinationContents = UnityEditor.PrefabUtility.LoadPrefabContents(destinationPath);
                try
                {
                    var sourceAsset = UnityEditor.AssetDatabase.LoadAssetAtPath<UnityEngine.GameObject>(sourcePath);
                    var nested = (UnityEngine.GameObject)UnityEditor.PrefabUtility.InstantiatePrefab(sourceAsset, destinationContents.scene);
                    nested.transform.SetParent(destinationContents.transform, false);
                    nested.GetComponent<UnityEngine.BoxCollider>().center = UnityEngine.Vector3.one;
                    UnityEditor.PrefabUtility.RecordPrefabInstancePropertyModifications(nested.GetComponent<UnityEngine.BoxCollider>());
                    UnityEditor.PrefabUtility.SaveAsPrefabAsset(destinationContents, destinationPath, out var success);
                    Assert.That(success, Is.True);
                }
                finally { UnityEditor.PrefabUtility.UnloadPrefabContents(destinationContents); }

                var sourceContents = UnityEditor.PrefabUtility.LoadPrefabContents(sourcePath);
                try
                {
                    UnityEngine.Object.DestroyImmediate(sourceContents.GetComponent<UnityEngine.BoxCollider>());
                    UnityEditor.PrefabUtility.SaveAsPrefabAsset(sourceContents, sourcePath, out var success);
                    Assert.That(success, Is.True);
                }
                finally { UnityEditor.PrefabUtility.UnloadPrefabContents(sourceContents); }
                UnityEditor.AssetDatabase.ImportAsset(destinationPath, UnityEditor.ImportAssetOptions.ForceUpdate);

                var cleanup = Collect(new PrefabProvider()).Tools.Single(t => t.Name == "prefab.remove_unused_overrides");
                var dryRun = cleanup.Handler(Ctx(new JObject
                {
                    ["path"] = destinationPath,
                    ["dryRun"] = true,
                }), CancellationToken.None).AsTask().GetAwaiter().GetResult();
                Assert.That((int)dryRun.StructuredContent["instanceCount"], Is.EqualTo(1));
                Assert.That((int)dryRun.StructuredContent["removedUnusedPropertyOverrideCount"], Is.GreaterThanOrEqualTo(1));

                var apply = cleanup.Handler(Ctx(new JObject
                {
                    ["path"] = destinationPath,
                    ["dryRun"] = false,
                    ["save"] = true,
                }), CancellationToken.None).AsTask().GetAwaiter().GetResult();
                Assert.That((bool)apply.StructuredContent["saved"], Is.True);
                Assert.That((bool)apply.StructuredContent["readback"]["verified"], Is.True);
                Assert.That((int)apply.StructuredContent["readback"]["propertyOverrideCount"],
                    Is.EqualTo((int)apply.StructuredContent["propertyOverrideCountAfter"]));
                Assert.That((int)apply.StructuredContent["removedUnusedPropertyOverrideCount"], Is.GreaterThanOrEqualTo(1));
            }
            finally
            {
                UnityEditor.AssetDatabase.DeleteAsset(folder);
            }
        }

        [Test]
        public void Read_RejectsMissingPrefab()
        {
            var tool = Collect(new PrefabProvider()).Tools.First(t => t.Name == "prefab.read");
            var args = new JObject { ["path"] = "Assets/__does_not_exist__.prefab" };
            var ctx = Ctx(args);
            Assert.ThrowsAsync<McpToolException>(async () => await tool.Handler(ctx, CancellationToken.None));
        }

        [Test]
        public void Read_DefaultsToHierarchyAndCanTargetComponentDetails()
        {
            const string folder = "Assets/__prefab_read_test__";
            const string path = folder + "/Target.prefab";
            if (!UnityEditor.AssetDatabase.IsValidFolder(folder))
                UnityEditor.AssetDatabase.CreateFolder("Assets", "__prefab_read_test__");
            var root = new UnityEngine.GameObject("Target");
            var child = new UnityEngine.GameObject("Child");
            child.transform.SetParent(root.transform);
            child.AddComponent<UnityEngine.BoxCollider>();
            UnityEditor.PrefabUtility.SaveAsPrefabAsset(root, path);
            UnityEngine.Object.DestroyImmediate(root);

            try
            {
                var read = Collect(new PrefabProvider()).Tools.First(t => t.Name == "prefab.read");
                Assert.That(read.InputSchema["properties"]["objectPath"], Is.Not.Null);
                var hierarchy = read.Handler(Ctx(new JObject { ["path"] = path }), CancellationToken.None)
                    .AsTask().GetAwaiter().GetResult();
                Assert.That(hierarchy.StructuredContent["root"]["components"], Is.Null);

                var detail = read.Handler(Ctx(new JObject
                {
                    ["path"] = path,
                    ["objectPath"] = "Target/Child",
                    ["includeComponents"] = true,
                }), CancellationToken.None).AsTask().GetAwaiter().GetResult();
                Assert.That((string)detail.StructuredContent["root"]["name"], Is.EqualTo("Child"));
                Assert.That(((JArray)detail.StructuredContent["root"]["components"])
                    .Any(component => (string)component["type"] == "UnityEngine.BoxCollider"), Is.True);
                Assert.That(detail.StructuredContent["root"]["components"][0]["properties"], Is.Null);

                var properties = read.Handler(Ctx(new JObject
                {
                    ["path"] = path,
                    ["objectPath"] = "Target/Child",
                    ["includeProperties"] = true,
                    ["componentTypes"] = new JArray("BoxCollider"),
                    ["propertyLimit"] = 1,
                }), CancellationToken.None).AsTask().GetAwaiter().GetResult();
                Assert.That((int)properties.StructuredContent["root"]["components"][0]["propertyReturned"], Is.EqualTo(1));
            }
            finally
            {
                UnityEditor.AssetDatabase.DeleteAsset(folder);
            }
        }

        [Test]
        public void Write_RejectsMissingPrefab()
        {
            var tool = Collect(new PrefabProvider()).Tools.First(t => t.Name == "prefab.write");
            var args = new JObject
            {
                ["path"] = "Assets/__does_not_exist__.prefab",
                ["operations"] = new JArray { new JObject { ["type"] = "rename_gameobject", ["path"] = "Root", ["newName"] = "X" } },
            };
            var ctx = Ctx(args);
            Assert.ThrowsAsync<McpToolException>(async () => await tool.Handler(ctx, CancellationToken.None));
        }

        [Test]
        public void Write_RejectsMissingOperations()
        {
            var tool = Collect(new PrefabProvider()).Tools.First(t => t.Name == "prefab.write");
            var args = new JObject { ["path"] = "Assets/x.prefab" };
            var ctx = Ctx(args);
            Assert.ThrowsAsync<McpToolException>(async () => await tool.Handler(ctx, CancellationToken.None));
        }

        [Test]
        public void Write_RejectsDeleteOfRoot()
        {
            var tool = Collect(new PrefabProvider()).Tools.First(t => t.Name == "prefab.write");
            var args = new JObject
            {
                ["path"] = "Assets/x.prefab",
                ["operations"] = new JArray
                {
                    new JObject { ["type"] = "delete_gameobject", ["path"] = "Root" },
                },
            };
            var ctx = Ctx(args);
            var ex = Assert.ThrowsAsync<McpToolException>(async () => await tool.Handler(ctx, CancellationToken.None));
            Assert.That(ex.Message, Does.Contain("delete_gameobject"));
        }

        [Test]
        public void Write_AddComponent_RoundTripsThroughRead()
        {
            const string folder = "Assets/__prefab_test__";
            const string path = "Assets/__prefab_test__/RoundTrip.prefab";

            // Set up: throwaway GameObject -> prefab on disk.
            if (!UnityEditor.AssetDatabase.IsValidFolder(folder))
                UnityEditor.AssetDatabase.CreateFolder("Assets", "__prefab_test__");
            var temp = new UnityEngine.GameObject("RoundTrip");
            UnityEditor.PrefabUtility.SaveAsPrefabAsset(temp, path);
            UnityEngine.Object.DestroyImmediate(temp);

            try
            {
                var write = Collect(new PrefabProvider()).Tools.First(t => t.Name == "prefab.write");
                var writeArgs = new JObject
                {
                    ["path"] = path,
                    ["operations"] = new JArray
                    {
                        new JObject
                        {
                            ["type"] = "add_component",
                            ["path"] = "RoundTrip",
                            ["componentType"] = "UnityEngine.BoxCollider",
                        },
                    },
                };
                var writeResult = write.Handler(Ctx(writeArgs), CancellationToken.None).AsTask().GetAwaiter().GetResult();
                Assert.That((int)writeResult.StructuredContent["succeeded"], Is.EqualTo(1));
                Assert.That((bool)writeResult.StructuredContent["saved"], Is.True);

                var read = Collect(new PrefabProvider()).Tools.First(t => t.Name == "prefab.read");
                var readResult = read.Handler(Ctx(new JObject { ["path"] = path, ["includeComponents"] = true }), CancellationToken.None).AsTask().GetAwaiter().GetResult();
                var components = (JArray)readResult.StructuredContent["root"]["components"];
                var hasBox = components.Any(c => (string)c["type"] == "UnityEngine.BoxCollider");
                Assert.That(hasBox, Is.True, "BoxCollider not persisted to prefab");
            }
            finally
            {
                UnityEditor.AssetDatabase.DeleteAsset(path);
                UnityEditor.AssetDatabase.DeleteAsset(folder);
            }
        }

        [Test]
        public void Write_SetPropertyResolvesHierarchyComponentReference()
        {
            const string folder = "Assets/__prefab_reference_test__";
            const string path = folder + "/Reference.prefab";
            if (!UnityEditor.AssetDatabase.IsValidFolder(folder))
                UnityEditor.AssetDatabase.CreateFolder("Assets", "__prefab_reference_test__");

            var root = new UnityEngine.GameObject("Reference");
            root.AddComponent<UnityEngine.FixedJoint>();
            var child = new UnityEngine.GameObject("Child");
            child.transform.SetParent(root.transform);
            child.AddComponent<UnityEngine.Rigidbody>();
            UnityEditor.PrefabUtility.SaveAsPrefabAsset(root, path);
            UnityEngine.Object.DestroyImmediate(root);

            try
            {
                var write = Collect(new PrefabProvider()).Tools.First(t => t.Name == "prefab.write");
                var result = write.Handler(Ctx(new JObject
                {
                    ["path"] = path,
                    ["operations"] = new JArray
                    {
                        new JObject
                        {
                            ["type"] = "set_property",
                            ["path"] = "Reference",
                            ["componentType"] = typeof(UnityEngine.FixedJoint).FullName,
                            ["property"] = "m_ConnectedBody",
                            ["value"] = new JObject
                            {
                                ["hierarchyPath"] = "Reference/Child",
                                ["componentType"] = typeof(UnityEngine.Rigidbody).FullName,
                            },
                        },
                    },
                }), CancellationToken.None).AsTask().GetAwaiter().GetResult();

                Assert.That((int)result.StructuredContent["succeeded"], Is.EqualTo(1));
                var prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<UnityEngine.GameObject>(path);
                var connectedBody = prefab.GetComponent<UnityEngine.FixedJoint>().connectedBody;
                Assert.That(connectedBody, Is.Not.Null);
                Assert.That(connectedBody.gameObject.name, Is.EqualTo("Child"));
            }
            finally
            {
                UnityEditor.AssetDatabase.DeleteAsset(path);
                UnityEditor.AssetDatabase.DeleteAsset(folder);
            }
        }

        [Test]
        public void Write_AtomicByDefault_DoesNotSaveOnPartialFailure()
        {
            const string folder = "Assets/__prefab_atomic_test__";
            const string path = "Assets/__prefab_atomic_test__/Atomic.prefab";

            if (!UnityEditor.AssetDatabase.IsValidFolder(folder))
                UnityEditor.AssetDatabase.CreateFolder("Assets", "__prefab_atomic_test__");
            var temp = new UnityEngine.GameObject("Atomic");
            UnityEditor.PrefabUtility.SaveAsPrefabAsset(temp, path);
            UnityEngine.Object.DestroyImmediate(temp);

            try
            {
                var write = Collect(new PrefabProvider()).Tools.First(t => t.Name == "prefab.write");
                var args = new JObject
                {
                    ["path"] = path,
                    ["operations"] = new JArray
                    {
                        new JObject
                        {
                            ["type"] = "add_component",
                            ["path"] = "Atomic",
                            ["componentType"] = "UnityEngine.BoxCollider",
                        },
                        new JObject
                        {
                            ["type"] = "add_component",
                            ["path"] = "Atomic",
                            ["componentType"] = "DoesNotExistComponentType",
                        },
                    },
                };
                var result = write.Handler(Ctx(args), CancellationToken.None).AsTask().GetAwaiter().GetResult();
                Assert.That((int)result.StructuredContent["failed"], Is.EqualTo(1));
                Assert.That((bool)result.StructuredContent["saved"], Is.False);
                Assert.That((string)result.StructuredContent["abortReason"], Does.Contain("atomic_failure"));

                // Verify on disk: the BoxCollider must NOT have been persisted.
                var read = Collect(new PrefabProvider()).Tools.First(t => t.Name == "prefab.read");
                var readResult = read.Handler(Ctx(new JObject { ["path"] = path, ["includeComponents"] = true }), CancellationToken.None).AsTask().GetAwaiter().GetResult();
                var components = (JArray)readResult.StructuredContent["root"]["components"];
                var hasBox = components.Any(c => (string)c["type"] == "UnityEngine.BoxCollider");
                Assert.That(hasBox, Is.False, "BoxCollider was persisted despite atomic failure");
            }
            finally
            {
                UnityEditor.AssetDatabase.DeleteAsset(path);
                UnityEditor.AssetDatabase.DeleteAsset(folder);
            }
        }

        [Test]
        public void AddNestedPrefab_PersistsPrefabInstanceLink()
        {
            const string folder = "Assets/__prefab_nested_test__";
            const string sourcePath = folder + "/Source.prefab";
            const string destinationPath = folder + "/Destination.prefab";
            if (!UnityEditor.AssetDatabase.IsValidFolder(folder))
                UnityEditor.AssetDatabase.CreateFolder("Assets", "__prefab_nested_test__");

            var source = new UnityEngine.GameObject("Source");
            UnityEditor.PrefabUtility.SaveAsPrefabAsset(source, sourcePath);
            UnityEngine.Object.DestroyImmediate(source);
            var destination = new UnityEngine.GameObject("Destination");
            var socket = new UnityEngine.GameObject("Socket");
            socket.transform.SetParent(destination.transform);
            UnityEditor.PrefabUtility.SaveAsPrefabAsset(destination, destinationPath);
            UnityEngine.Object.DestroyImmediate(destination);

            try
            {
                var tool = Collect(new PrefabProvider()).Tools.Single(t => t.Name == "prefab.add_nested_prefab");
                tool.Handler(Ctx(new JObject
                {
                    ["path"] = destinationPath,
                    ["prefabPath"] = sourcePath,
                    ["parentPath"] = "Destination/Socket",
                    ["name"] = "Attached",
                    ["localPosition"] = new JObject { ["x"] = 1, ["y"] = 2, ["z"] = 3 },
                }), CancellationToken.None).AsTask().GetAwaiter().GetResult();

                var contents = UnityEditor.PrefabUtility.LoadPrefabContents(destinationPath);
                try
                {
                    var nested = contents.transform.Find("Socket/Attached");
                    Assert.That(nested, Is.Not.Null);
                    Assert.That(UnityEditor.PrefabUtility.IsAnyPrefabInstanceRoot(nested.gameObject), Is.True);
                    var nestedSource = UnityEditor.PrefabUtility.GetCorrespondingObjectFromSource(nested.gameObject);
                    Assert.That(UnityEditor.AssetDatabase.GetAssetPath(nestedSource), Is.EqualTo(sourcePath));
                    Assert.That(nested.localPosition, Is.EqualTo(new UnityEngine.Vector3(1, 2, 3)));
                }
                finally { UnityEditor.PrefabUtility.UnloadPrefabContents(contents); }
            }
            finally
            {
                UnityEditor.AssetDatabase.DeleteAsset(folder);
            }
        }

        [Test]
        public void CreateFromSceneObject_WrapsVisualAndPreservesSourceMesh()
        {
            var folderName = "__prefab_scene_object_test_" + System.Guid.NewGuid().ToString("N");
            var folder = "Assets/" + folderName;
            var dest = folder + "/Workbench.Model.prefab";
            UnityEditor.AssetDatabase.CreateFolder("Assets", folderName);

            var previousSetup = UnityEditor.SceneManagement.EditorSceneManager.GetSceneManagerSetup();
            var scene = UnityEditor.SceneManagement.EditorSceneManager.NewScene(
                UnityEditor.SceneManagement.NewSceneSetup.EmptyScene,
                UnityEditor.SceneManagement.NewSceneMode.Single);
            UnityEditor.SceneManagement.EditorSceneManager.SaveScene(scene, folder + "/Source.unity");

            var source = new UnityEngine.GameObject("Workbench");
            var mesh = new UnityEngine.Mesh { name = "RuntimeWorkbenchMesh" };
            mesh.vertices = new[]
            {
                UnityEngine.Vector3.zero,
                UnityEngine.Vector3.right,
                UnityEngine.Vector3.up,
            };
            mesh.triangles = new[] { 0, 1, 2 };
            mesh.RecalculateNormals();
            source.AddComponent<UnityEngine.MeshFilter>().sharedMesh = mesh;
            source.AddComponent<UnityEngine.MeshRenderer>();
            source.AddComponent<UnityEngine.MeshCollider>().sharedMesh = mesh;
            new UnityEngine.GameObject("Text").transform.SetParent(source.transform, false);
            new UnityEngine.GameObject("Service Marker").transform.SetParent(source.transform, false);
            var sourceDirtyBefore = scene.isDirty;

            try
            {
                var tool = Collect(new PrefabProvider()).Tools.Single(
                    t => t.Name == "prefab.create_from_scene_object");
                var args = new JObject
                {
                    ["path"] = "Workbench",
                    ["dest"] = dest,
                    ["rootName"] = "Workbench.Model",
                    ["visualChildName"] = "__PivotVisual",
                    ["excludePaths"] = new JArray("Service Marker"),
                };

                var dryRun = tool.Handler(Ctx(args), CancellationToken.None)
                    .AsTask().GetAwaiter().GetResult();
                Assert.That((bool)dryRun.StructuredContent["dryRun"], Is.True);
                Assert.That(UnityEditor.AssetDatabase.LoadMainAssetAtPath(dest), Is.Null);

                args["dryRun"] = false;
                args["confirm"] = true;
                tool.Handler(Ctx(args), CancellationToken.None).AsTask().GetAwaiter().GetResult();

                Assert.That(source.GetComponent<UnityEngine.MeshFilter>().sharedMesh, Is.SameAs(mesh));
                Assert.That(source.GetComponent<UnityEngine.MeshCollider>().sharedMesh, Is.SameAs(mesh));
                Assert.That(scene.isDirty, Is.EqualTo(sourceDirtyBefore));

                var contents = UnityEditor.PrefabUtility.LoadPrefabContents(dest);
                try
                {
                    Assert.That(contents.name, Is.EqualTo("Workbench.Model"));
                    var visual = contents.transform.Find("__PivotVisual");
                    Assert.That(visual, Is.Not.Null);
                    Assert.That(visual.localPosition, Is.EqualTo(UnityEngine.Vector3.zero));
                    Assert.That(visual.Find("Text"), Is.Not.Null);
                    Assert.That(visual.Find("Service Marker"), Is.Null);
                    Assert.That(visual.GetComponent<UnityEngine.MeshFilter>().sharedMesh, Is.Not.Null);
                    Assert.That(visual.GetComponent<UnityEngine.MeshFilter>().sharedMesh, Is.Not.SameAs(mesh));
                    Assert.That(visual.GetComponent<UnityEngine.MeshCollider>().sharedMesh,
                        Is.SameAs(visual.GetComponent<UnityEngine.MeshFilter>().sharedMesh));
                }
                finally { UnityEditor.PrefabUtility.UnloadPrefabContents(contents); }
            }
            finally
            {
                if (previousSetup.Length > 0)
                    UnityEditor.SceneManagement.EditorSceneManager.RestoreSceneManagerSetup(previousSetup);
                else
                    UnityEditor.SceneManagement.EditorSceneManager.NewScene(
                        UnityEditor.SceneManagement.NewSceneSetup.EmptyScene,
                        UnityEditor.SceneManagement.NewSceneMode.Single);
                UnityEditor.AssetDatabase.DeleteAsset(folder);
                if (mesh != null)
                    UnityEngine.Object.DestroyImmediate(mesh);
            }
        }

        [Test]
        public void Create_RejectsNonPrefabExtension()
        {
            var tool = Collect(new PrefabProvider()).Tools.First(t => t.Name == "prefab.create");
            var args = new JObject { ["src"] = "Assets/x.prefab", ["dest"] = "Assets/y.asset" };
            var ctx = Ctx(args);
            var ex = Assert.ThrowsAsync<McpToolException>(async () => await tool.Handler(ctx, CancellationToken.None));
            Assert.That(ex.Message, Does.Contain(".prefab"));
        }

        [Test]
        public void Create_RejectsSameSourceAndDest()
        {
            var tool = Collect(new PrefabProvider()).Tools.First(t => t.Name == "prefab.create");
            var args = new JObject { ["src"] = "Assets/x.prefab", ["dest"] = "Assets/x.prefab", ["overwrite"] = true };
            var ctx = Ctx(args);
            var ex = Assert.ThrowsAsync<McpToolException>(async () => await tool.Handler(ctx, CancellationToken.None));
            Assert.That(ex.Message, Does.Contain("different"));
        }

        [Test]
        public void Create_OverwriteReplacesExistingPrefab()
        {
            const string folder = "Assets/__prefab_create_overwrite_test__";
            const string src = "Assets/__prefab_create_overwrite_test__/Source.prefab";
            const string dest = "Assets/__prefab_create_overwrite_test__/Dest.prefab";

            if (!UnityEditor.AssetDatabase.IsValidFolder(folder))
                UnityEditor.AssetDatabase.CreateFolder("Assets", "__prefab_create_overwrite_test__");

            var sourceRoot = new UnityEngine.GameObject("SourceRoot");
            sourceRoot.AddComponent<UnityEngine.BoxCollider>();
            var destRoot = new UnityEngine.GameObject("DestRoot");
            UnityEditor.PrefabUtility.SaveAsPrefabAsset(sourceRoot, src);
            UnityEditor.PrefabUtility.SaveAsPrefabAsset(destRoot, dest);
            UnityEngine.Object.DestroyImmediate(sourceRoot);
            UnityEngine.Object.DestroyImmediate(destRoot);

            try
            {
                var originalGuid = UnityEditor.AssetDatabase.AssetPathToGUID(dest);
                var tool = Collect(new PrefabProvider()).Tools.First(t => t.Name == "prefab.create");
                var args = new JObject { ["src"] = src, ["dest"] = dest, ["overwrite"] = true };
                tool.Handler(Ctx(args), CancellationToken.None).AsTask().GetAwaiter().GetResult();

                var contents = UnityEditor.PrefabUtility.LoadPrefabContents(dest);
                try
                {
                    Assert.That(contents.GetComponent<UnityEngine.BoxCollider>(), Is.Not.Null);
                }
                finally { UnityEditor.PrefabUtility.UnloadPrefabContents(contents); }
                Assert.That(UnityEditor.AssetDatabase.AssetPathToGUID(dest), Is.EqualTo(originalGuid));
            }
            finally
            {
                UnityEditor.AssetDatabase.DeleteAsset(folder);
            }
        }

        [Test]
        public void Create_RejectsUnknownMode()
        {
            var tool = Collect(new PrefabProvider()).Tools.First(t => t.Name == "prefab.create");
            var args = new JObject { ["src"] = "Assets/x.prefab", ["dest"] = "Assets/y.prefab", ["mode"] = "clone" };
            var ctx = Ctx(args);
            var ex = Assert.ThrowsAsync<McpToolException>(async () => await tool.Handler(ctx, CancellationToken.None));
            Assert.That(ex.Message, Does.Contain("mode"));
        }

        [Test]
        public void Create_RejectsMissingSource()
        {
            var tool = Collect(new PrefabProvider()).Tools.First(t => t.Name == "prefab.create");
            var args = new JObject { ["src"] = "Assets/__does_not_exist__.prefab", ["dest"] = "Assets/__out.prefab" };
            var ctx = Ctx(args);
            Assert.ThrowsAsync<McpToolException>(async () => await tool.Handler(ctx, CancellationToken.None));
        }

        private static CollectingSink Collect(IToolProvider p) { var s = new CollectingSink(); p.RegisterTools(s); return s; }

        private static ToolContext Ctx(JObject args) => new(args, new NoopProgressReporter(), new FakeMainThreadPump(),
            new FakeFrameWaiter(), null, new FakeLogSink(), "req");

        private sealed class CollectingSink : IToolRegistration
        {
            public List<ToolDescriptor> Tools = new();
            public void Register(ToolDescriptor descriptor) => Tools.Add(descriptor);
        }
    }
}
