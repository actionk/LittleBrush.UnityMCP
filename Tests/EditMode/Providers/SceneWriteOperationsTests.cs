using System.Globalization;
using System.Linq;
using LittleBrushGames.Mcp.Editor.Dispatch;
using LittleBrushGames.Mcp.Editor.Providers;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace LittleBrushGames.Mcp.Tests.Providers
{
    public enum SceneWriteTestEnum { A, B }

    public sealed class SceneWriteTestComponent : MonoBehaviour
    {
        public GameObject target;
        public SceneWriteTestEnum mode;
        public int[] values;
    }

    public class SceneWriteOperationsTests
    {
        [Test]
        public void ApplyToRoot_RejectsUnboundedOperationBatches()
        {
            var root = new GameObject("Root");
            try
            {
                var operations = new JArray(Enumerable.Range(0, SceneWriteOperations.MaxOperations + 1)
                    .Select(_ => new JObject { ["type"] = "set_active", ["path"] = "Root", ["active"] = true }));

                var ex = Assert.Throws<McpToolException>(() => SceneWriteOperations.ApplyToRoot(root, operations));
                Assert.That(ex.Message, Does.Contain(SceneWriteOperations.MaxOperations.ToString()));
            }
            finally { Object.DestroyImmediate(root); }
        }

        [Test]
        public void ApplyToRoot_AddRemoveComponent_RoundTrips()
        {
            var root = new GameObject("Root");
            try
            {
                var addOps = new JArray
                {
                    new JObject
                    {
                        ["type"] = "add_component",
                        ["path"] = "Root",
                        ["componentType"] = "UnityEngine.BoxCollider",
                    },
                };
                var addResult = SceneWriteOperations.ApplyToRoot(root, addOps);
                Assert.That((int)addResult["succeeded"], Is.EqualTo(1));
                Assert.That(root.GetComponent<BoxCollider>(), Is.Not.Null);

                var removeOps = new JArray
                {
                    new JObject
                    {
                        ["type"] = "remove_component",
                        ["path"] = "Root",
                        ["componentType"] = "UnityEngine.BoxCollider",
                    },
                };
                var removeResult = SceneWriteOperations.ApplyToRoot(root, removeOps);
                Assert.That((int)removeResult["succeeded"], Is.EqualTo(1));
                Assert.That(root.GetComponent<BoxCollider>(), Is.Null);
            }
            finally { Object.DestroyImmediate(root); }
        }

        [Test]
        public void ApplyToRoot_SetPropertyTargetsRepeatedComponentByIndex()
        {
            var root = new GameObject("Root");
            var first = root.AddComponent<BoxCollider>();
            var second = root.AddComponent<BoxCollider>();
            try
            {
                var result = SceneWriteOperations.ApplyToRoot(root, new JArray
                {
                    new JObject
                    {
                        ["type"] = "set_property",
                        ["path"] = "Root",
                        ["componentType"] = "UnityEngine.BoxCollider",
                        ["componentIndex"] = 1,
                        ["property"] = "m_IsTrigger",
                        ["value"] = true,
                    },
                });
                Assert.That((int)result["succeeded"], Is.EqualTo(1));
                Assert.That(first.isTrigger, Is.False);
                Assert.That(second.isTrigger, Is.True);
            }
            finally { Object.DestroyImmediate(root); }
        }

        [Test]
        public void ApplyToRoot_RejectsInstantiatePrefab()
        {
            var root = new GameObject("Root");
            try
            {
                var ops = new JArray
                {
                    new JObject
                    {
                        ["type"] = "instantiate_prefab",
                        ["assetPath"] = "Assets/x.prefab",
                    },
                };
                var result = SceneWriteOperations.ApplyToRoot(root, ops);
                Assert.That((int)result["failed"], Is.EqualTo(1));
                var err = result["results"][0]["error"]?.ToString() ?? "";
                Assert.That(err, Does.Contain("not supported"));
            }
            finally { Object.DestroyImmediate(root); }
        }

        [Test]
        public void ApplyToRoot_CreateGameObjectWithoutParent_Rejects()
        {
            var root = new GameObject("Root");
            try
            {
                var ops = new JArray
                {
                    new JObject
                    {
                        ["type"] = "create_gameobject",
                        ["name"] = "Child",
                    },
                };
                var result = SceneWriteOperations.ApplyToRoot(root, ops);
                Assert.That((int)result["failed"], Is.EqualTo(1));
            }
            finally { Object.DestroyImmediate(root); }
        }

        [Test]
        public void ApplyToRoot_CreateGameObjectUnderParent_Succeeds()
        {
            var root = new GameObject("Root");
            try
            {
                var ops = new JArray
                {
                    new JObject
                    {
                        ["type"] = "create_gameobject",
                        ["name"] = "Child",
                        ["parentPath"] = "Root",
                    },
                };
                var result = SceneWriteOperations.ApplyToRoot(root, ops);
                Assert.That((int)result["succeeded"], Is.EqualTo(1));
                Assert.That(root.transform.Find("Child"), Is.Not.Null);
            }
            finally { Object.DestroyImmediate(root); }
        }

        [Test]
        public void ApplyToRoot_ReparentWithoutWorldPositionStaysPreservesLocalTransform()
        {
            var root = new GameObject("Root");
            var firstParent = new GameObject("First");
            var secondParent = new GameObject("Second");
            var child = new GameObject("Child");
            firstParent.transform.SetParent(root.transform);
            secondParent.transform.SetParent(root.transform);
            child.transform.SetParent(firstParent.transform);
            child.transform.localPosition = new Vector3(1, 2, 3);
            child.transform.localRotation = Quaternion.Euler(10, 20, 30);
            child.transform.localScale = new Vector3(2, 3, 4);
            var expectedRotation = child.transform.localRotation;
            try
            {
                var ops = new JArray
                {
                    new JObject
                    {
                        ["type"] = "reparent",
                        ["path"] = "Root/First/Child",
                        ["newParentPath"] = "Root/Second",
                        ["worldPositionStays"] = false,
                    },
                };
                var result = SceneWriteOperations.ApplyToRoot(root, ops);
                Assert.That((int)result["succeeded"], Is.EqualTo(1));
                Assert.That(child.transform.localPosition, Is.EqualTo(new Vector3(1, 2, 3)));
                Assert.That(Quaternion.Angle(child.transform.localRotation, expectedRotation), Is.LessThan(0.001f));
                Assert.That(child.transform.localScale, Is.EqualTo(new Vector3(2, 3, 4)));
            }
            finally { Object.DestroyImmediate(root); }
        }

        [Test]
        public void ApplyToRoot_SetObjectReferenceRejectsMissingAsset()
        {
            var root = new GameObject("Root");
            root.AddComponent<MeshFilter>();
            try
            {
                var ops = new JArray
                {
                    new JObject
                    {
                        ["type"] = "set_property",
                        ["path"] = "Root",
                        ["componentType"] = "UnityEngine.MeshFilter",
                        ["property"] = "m_Mesh",
                        ["value"] = "Assets/__missing_mesh__.asset",
                    },
                };
                var result = SceneWriteOperations.ApplyToRoot(root, ops);
                Assert.That((int)result["failed"], Is.EqualTo(1));
                var err = result["results"][0]["error"]?.ToString() ?? "";
                Assert.That(err, Does.Contain("Object reference asset not found"));
            }
            finally { Object.DestroyImmediate(root); }
        }

        [Test]
        public void ApplyToRoot_SetObjectReferenceRejectsMissingGuid()
        {
            var root = new GameObject("Root");
            root.AddComponent<MeshFilter>();
            try
            {
                var ops = new JArray
                {
                    new JObject
                    {
                        ["type"] = "set_property",
                        ["path"] = "Root",
                        ["componentType"] = "UnityEngine.MeshFilter",
                        ["property"] = "m_Mesh",
                        ["value"] = new JObject { ["guid"] = "00000000000000000000000000000000" },
                    },
                };
                var result = SceneWriteOperations.ApplyToRoot(root, ops);
                Assert.That((int)result["failed"], Is.EqualTo(1));
                Assert.That(result["results"][0]["error"]?.ToString(), Does.Contain("GUID not found"));
            }
            finally { Object.DestroyImmediate(root); }
        }

        [Test]
        public void ApplyToRoot_SetObjectReferenceSelectsSubAssetByLocalId()
        {
            const string folder = "Assets/__scene_write_subasset_test__";
            const string path = folder + "/Meshes.asset";
            if (!AssetDatabase.IsValidFolder(folder))
                AssetDatabase.CreateFolder("Assets", "__scene_write_subasset_test__");
            var main = new Mesh { name = "Main" };
            var sub = new Mesh { name = "Sub" };
            AssetDatabase.CreateAsset(main, path);
            AssetDatabase.AddObjectToAsset(sub, path);
            AssetDatabase.SaveAssets();
            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(sub, out var guid, out long localId);
            var root = new GameObject("Root");
            var filter = root.AddComponent<MeshFilter>();

            try
            {
                var result = SceneWriteOperations.ApplyToRoot(root, new JArray
                {
                    new JObject
                    {
                        ["type"] = "set_property",
                        ["path"] = "Root",
                        ["componentType"] = "UnityEngine.MeshFilter",
                        ["property"] = "m_Mesh",
                        ["value"] = new JObject
                        {
                            ["guid"] = guid,
                            ["localId"] = localId.ToString(CultureInfo.InvariantCulture),
                        },
                    },
                });

                Assert.That((int)result["succeeded"], Is.EqualTo(1));
                Assert.That(filter.sharedMesh, Is.SameAs(sub));
            }
            finally
            {
                Object.DestroyImmediate(root);
                AssetDatabase.DeleteAsset(folder);
            }
        }

        [Test]
        public void ApplyToRoot_SetObjectReferenceRejectsIncompatibleAssetType()
        {
            const string folder = "Assets/__scene_write_object_type_test__";
            const string path = folder + "/Texture.asset";
            if (!AssetDatabase.IsValidFolder(folder))
                AssetDatabase.CreateFolder("Assets", "__scene_write_object_type_test__");
            AssetDatabase.CreateAsset(new Texture2D(1, 1), path);
            var root = new GameObject("Root");
            var filter = root.AddComponent<MeshFilter>();
            var original = new Mesh();
            filter.sharedMesh = original;

            try
            {
                var result = SceneWriteOperations.ApplyToRoot(root, new JArray
                {
                    new JObject
                    {
                        ["type"] = "set_property",
                        ["path"] = "Root",
                        ["componentType"] = "UnityEngine.MeshFilter",
                        ["property"] = "m_Mesh",
                        ["value"] = path,
                    },
                });

                Assert.That((int)result["failed"], Is.EqualTo(1));
                Assert.That(result["results"][0]["error"]?.ToString(), Does.Contain("not compatible"));
                Assert.That(filter.sharedMesh, Is.SameAs(original));
            }
            finally
            {
                Object.DestroyImmediate(root);
                Object.DestroyImmediate(original);
                AssetDatabase.DeleteAsset(folder);
            }
        }

        [Test]
        public void ApplyToRoot_FailureRollsBackEarlierOperations()
        {
            var root = new GameObject("Root");
            try
            {
                var ops = new JArray
                {
                    new JObject
                    {
                        ["type"] = "create_gameobject",
                        ["name"] = "Child",
                        ["parentPath"] = "Root",
                    },
                    new JObject
                    {
                        ["type"] = "instantiate_prefab",
                        ["assetPath"] = "Assets/x.prefab",
                    },
                };

                var result = SceneWriteOperations.ApplyToRoot(root, ops);

                Assert.That((int)result["failed"], Is.EqualTo(1));
                Assert.That((bool)result["rolledBack"], Is.True);
                Assert.That(root.transform.Find("Child"), Is.Null);
            }
            finally { Object.DestroyImmediate(root); }
        }

        [Test]
        public void Apply_SameBatchCanTargetNewRoot()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            try
            {
                var ops = new JArray
                {
                    new JObject
                    {
                        ["type"] = "create_gameobject",
                        ["name"] = "NewRoot",
                    },
                    new JObject
                    {
                        ["type"] = "create_gameobject",
                        ["name"] = "Child",
                        ["parentPath"] = "NewRoot",
                    },
                };

                var result = SceneWriteOperations.Apply(scene, ops);

                Assert.That((int)result["succeeded"], Is.EqualTo(2));
                Assert.That(GameObject.Find("NewRoot")?.transform.Find("Child"), Is.Not.Null);
            }
            finally
            {
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            }
        }

        [Test]
        public void ApplyToRoot_CanTargetDuplicateNameByInstanceId()
        {
            var root = new GameObject("Root");
            var first = new GameObject("Child");
            var second = new GameObject("Child");
            first.transform.SetParent(root.transform);
            second.transform.SetParent(root.transform);
            try
            {
                var ops = new JArray
                {
                    new JObject
                    {
                        ["type"] = "rename_gameobject",
                        ["entityId"] = UnitySerializer.ToEntityIdString(second),
                        ["newName"] = "Target",
                    },
                };

                var result = SceneWriteOperations.ApplyToRoot(root, ops);

                Assert.That((int)result["succeeded"], Is.EqualTo(1));
                Assert.That(first.name, Is.EqualTo("Child"));
                Assert.That(second.name, Is.EqualTo("Target"));
            }
            finally { Object.DestroyImmediate(root); }
        }

        [Test]
        public void ApplyToRoot_SetPropertyHandlesEnumObjectRefAndArray()
        {
            var root = new GameObject("Root");
            var target = new GameObject("Target");
            target.transform.SetParent(root.transform);
            var component = root.AddComponent<SceneWriteTestComponent>();
            try
            {
                var ops = new JArray
                {
                    new JObject
                    {
                        ["type"] = "set_property",
                        ["path"] = "Root",
                        ["componentType"] = typeof(SceneWriteTestComponent).FullName,
                        ["property"] = "mode",
                        ["value"] = "B",
                    },
                    new JObject
                    {
                        ["type"] = "set_property",
                        ["path"] = "Root",
                        ["componentType"] = typeof(SceneWriteTestComponent).FullName,
                        ["property"] = "target",
                        ["value"] = new JObject { ["entityId"] = UnitySerializer.ToEntityIdString(target) },
                    },
                    new JObject
                    {
                        ["type"] = "set_property",
                        ["path"] = "Root",
                        ["componentType"] = typeof(SceneWriteTestComponent).FullName,
                        ["property"] = "values",
                        ["value"] = new JArray(1, 2, 3),
                    },
                    new JObject
                    {
                        ["type"] = "set_property",
                        ["path"] = "Root",
                        ["componentType"] = typeof(SceneWriteTestComponent).FullName,
                        ["property"] = "values.Array.size",
                        ["value"] = 2,
                    },
                };

                var result = SceneWriteOperations.ApplyToRoot(root, ops);

                Assert.That((int)result["succeeded"], Is.EqualTo(4));
                Assert.That(component.mode, Is.EqualTo(SceneWriteTestEnum.B));
                Assert.That(component.target, Is.EqualTo(target));
                Assert.That(component.values, Is.EqualTo(new[] { 1, 2 }));
            }
            finally { Object.DestroyImmediate(root); }
        }
    }
}
