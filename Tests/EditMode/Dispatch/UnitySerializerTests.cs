using System.Linq;
using System.Globalization;
using LittleBrushGames.Mcp.Editor.Dispatch;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace LittleBrushGames.Mcp.Tests.Dispatch
{
    public class UnitySerializerTests
    {
        [Test]
        public void Vector3_SerializesAsObjectWithXyz_NoRecursion()
        {
            var v = new Vector3(1f, 2f, 3f);
            var json = UnitySerializer.ToJson(v);
            var obj = (JObject)json;
            Assert.That((float)obj["x"], Is.EqualTo(1f));
            Assert.That((float)obj["y"], Is.EqualTo(2f));
            Assert.That((float)obj["z"], Is.EqualTo(3f));
            Assert.That(obj.Properties().Count(), Is.EqualTo(3));
        }

        [Test]
        public void Color_SerializesAsRgba()
        {
            var c = new Color(0.25f, 0.5f, 0.75f, 1f);
            var obj = (JObject)UnitySerializer.ToJson(c);
            Assert.That((float)obj["r"], Is.EqualTo(0.25f));
            Assert.That((float)obj["a"], Is.EqualTo(1f));
            Assert.That(obj.Properties().Count(), Is.EqualTo(4));
        }

        [Test]
        public void Quaternion_SerializesAsXyzw()
        {
            var q = new Quaternion(0.1f, 0.2f, 0.3f, 1f);
            var obj = (JObject)UnitySerializer.ToJson(q);
            Assert.That((float)obj["w"], Is.EqualTo(1f));
            Assert.That(obj.Properties().Count(), Is.EqualTo(4));
        }

        [Test]
        public void Vector3_RoundTrips()
        {
            var v = new Vector3(1.5f, -2.25f, 3.75f);
            var json = UnitySerializer.ToJson(v).ToString();
            var parsed = JsonConvert.DeserializeObject<Vector3>(json, UnitySerializer.Settings);
            Assert.That(parsed, Is.EqualTo(v));
        }

        [Test]
        public void Color_RoundTrips()
        {
            var c = new Color(0.1f, 0.2f, 0.3f, 0.4f);
            var json = UnitySerializer.ToJson(c).ToString();
            var parsed = JsonConvert.DeserializeObject<Color>(json, UnitySerializer.Settings);
            Assert.That(parsed.r, Is.EqualTo(0.1f).Within(1e-6f));
            Assert.That(parsed.a, Is.EqualTo(0.4f).Within(1e-6f));
        }

        [Test]
        public void Quaternion_RoundTrips()
        {
            var q = new Quaternion(0.1f, 0.2f, 0.3f, 0.9f);
            var json = UnitySerializer.ToJson(q).ToString();
            var parsed = JsonConvert.DeserializeObject<Quaternion>(json, UnitySerializer.Settings);
            Assert.That(parsed.w, Is.EqualTo(0.9f).Within(1e-6f));
        }

        [Test]
        public void LayerMask_RoundTrips()
        {
            LayerMask m = 0b1010;
            var json = UnitySerializer.ToJson(m).ToString();
            var parsed = JsonConvert.DeserializeObject<LayerMask>(json, UnitySerializer.Settings);
            Assert.That(parsed.value, Is.EqualTo(0b1010));
        }

        [Test]
        public void Bounds_RoundTrips()
        {
            var b = new Bounds(new Vector3(1, 2, 3), new Vector3(4, 5, 6));
            var json = UnitySerializer.ToJson(b).ToString();
            var parsed = JsonConvert.DeserializeObject<Bounds>(json, UnitySerializer.Settings);
            Assert.That(parsed.center, Is.EqualTo(b.center));
            Assert.That(parsed.size, Is.EqualTo(b.size));
        }

        [Test]
        public void Vector3_Read_MissingField_Throws()
        {
            var ex = Assert.Throws<JsonSerializationException>(() =>
                JsonConvert.DeserializeObject<Vector3>("{\"x\":1,\"y\":2}", UnitySerializer.Settings));
            Assert.That(ex.Message, Does.Contain("Vector3"));
            Assert.That(ex.Message, Does.Contain("z"));
        }

        [Test]
        public void Vector3_Read_NullToken_ReturnsDefault()
        {
            var parsed = JsonConvert.DeserializeObject<Vector3?>("null", UnitySerializer.Settings);
            Assert.That(parsed, Is.Null);
        }

        [Test]
        public void Bounds_Read_MissingCenter_Throws()
        {
            var ex = Assert.Throws<JsonSerializationException>(() =>
                JsonConvert.DeserializeObject<Bounds>("{\"size\":{\"x\":1,\"y\":2,\"z\":3}}", UnitySerializer.Settings));
            Assert.That(ex.Message, Does.Contain("center"));
        }

        [Test]
        public void UnityObject_SerializesAsReference()
        {
            var go = new GameObject("RefTarget");
            try
            {
                var obj = (JObject)UnitySerializer.ToJson(go);
                Assert.That((string)obj["$type"], Is.EqualTo("objectRef"));
                Assert.That((string)obj["name"], Is.EqualTo("RefTarget"));
                Assert.That((string)obj["entityId"], Is.EqualTo(UnitySerializer.ToEntityIdString(go)));
                Assert.That(UnitySerializer.TryParseEntityId(obj["entityId"], out var parsed), Is.True);
                Assert.That(parsed, Is.EqualTo(go.GetEntityId()));
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        public void AssetSubObject_SerializesWithExactLocalId()
        {
            const string folder = "Assets/__serializer_subasset_test__";
            const string path = folder + "/Meshes.asset";
            if (!AssetDatabase.IsValidFolder(folder))
                AssetDatabase.CreateFolder("Assets", "__serializer_subasset_test__");
            var main = new Mesh { name = "Main" };
            var sub = new Mesh { name = "Sub" };
            AssetDatabase.CreateAsset(main, path);
            AssetDatabase.AddObjectToAsset(sub, path);
            AssetDatabase.SaveAssets();

            try
            {
                Assert.That(AssetDatabase.TryGetGUIDAndLocalFileIdentifier(sub, out _, out long localId), Is.True);
                var obj = (JObject)UnitySerializer.ToJson(sub);
                Assert.That((string)obj["localId"], Is.EqualTo(localId.ToString(CultureInfo.InvariantCulture)));
            }
            finally
            {
                AssetDatabase.DeleteAsset(folder);
            }
        }
    }
}
