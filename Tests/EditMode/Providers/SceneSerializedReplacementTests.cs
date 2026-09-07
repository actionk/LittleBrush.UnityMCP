using LittleBrushGames.Mcp.Editor.Dispatch;
using LittleBrushGames.Mcp.Editor.Providers;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace LittleBrushGames.Mcp.Tests.Providers
{
    public sealed class SceneSerializedReplacementTestComponent : MonoBehaviour
    {
        public string id;
        public string[] ids;
        public GameObject target;
    }

    public class SceneSerializedReplacementTests
    {
        [Test]
        public void Replace_UpdatesExactStringsAndLeavesSubstringMatchesAlone()
        {
            var scene = EditorSceneManager.NewPreviewScene();
            try
            {
                var root = new GameObject("Root");
                SceneManager.MoveGameObjectToScene(root, scene);
                var component = root.AddComponent<SceneSerializedReplacementTestComponent>();
                component.id = "old.id";
                component.ids = new[] { "old.id", "old.id.suffix" };

                var result = SceneSerializedReplacement.Replace(scene, "old.id", "new.id", false, false, 10);

                Assert.That((int)result["matchCount"], Is.EqualTo(2));
                Assert.That((int)result["changedCount"], Is.EqualTo(2));
                Assert.That(component.id, Is.EqualTo("new.id"));
                Assert.That(component.ids, Is.EqualTo(new[] { "new.id", "old.id.suffix" }));
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(scene);
            }
        }

        [Test]
        public void Replace_UpdatesObjectReferencesByEntityId()
        {
            var scene = EditorSceneManager.NewPreviewScene();
            try
            {
                var source = new GameObject("Source");
                var oldTarget = new GameObject("OldTarget");
                var newTarget = new GameObject("NewTarget");
                SceneManager.MoveGameObjectToScene(source, scene);
                SceneManager.MoveGameObjectToScene(oldTarget, scene);
                SceneManager.MoveGameObjectToScene(newTarget, scene);
                var component = source.AddComponent<SceneSerializedReplacementTestComponent>();
                component.target = oldTarget;

                var result = SceneSerializedReplacement.Replace(
                    scene,
                    UnitySerializer.ToEntityIdString(oldTarget),
                    UnitySerializer.ToEntityIdString(newTarget),
                    false,
                    false,
                    10);

                Assert.That((int)result["objectReferenceMatches"], Is.EqualTo(1));
                Assert.That(component.target, Is.SameAs(newTarget));
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(scene);
            }
        }

        [Test]
        public void Replace_DryRunDoesNotMutate()
        {
            var scene = EditorSceneManager.NewPreviewScene();
            try
            {
                var root = new GameObject("Root");
                SceneManager.MoveGameObjectToScene(root, scene);
                var component = root.AddComponent<SceneSerializedReplacementTestComponent>();
                component.id = "old.id";

                var result = SceneSerializedReplacement.Replace(scene, "old.id", "new.id", true, true, 10);

                Assert.That((int)result["matchCount"], Is.EqualTo(1));
                Assert.That((int)result["changedCount"], Is.EqualTo(0));
                Assert.That((bool)result["saved"], Is.False);
                Assert.That(component.id, Is.EqualTo("old.id"));
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(scene);
            }
        }

        [Test]
        public void ReplacePrefab_UpdatesSerializedValuesWithoutSavingAnActiveScene()
        {
            var root = new GameObject("PrefabRoot");
            try
            {
                var component = root.AddComponent<SceneSerializedReplacementTestComponent>();
                component.id = "old.id";

                var result = SceneSerializedReplacement.ReplacePrefab(
                    root,
                    "Assets/Test.prefab",
                    "old.id",
                    "new.id",
                    false,
                    false,
                    10);

                Assert.That((string)result["assetPath"], Is.EqualTo("Assets/Test.prefab"));
                Assert.That((int)result["matchCount"], Is.EqualTo(1));
                Assert.That(component.id, Is.EqualTo("new.id"));
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }
    }
}
