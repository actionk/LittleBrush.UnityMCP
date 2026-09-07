using System;
using System.Globalization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace LittleBrushGames.Mcp.Editor.Dispatch
{
    public static class UnitySerializer
    {
        public static JsonSerializerSettings Settings { get; } = BuildSettings();

        public static JsonSerializer Serializer { get; } = JsonSerializer.Create(Settings);

        private static JsonSerializerSettings BuildSettings()
        {
            var settings = new JsonSerializerSettings
            {
                NullValueHandling = NullValueHandling.Ignore,
                Culture = CultureInfo.InvariantCulture,
                Formatting = Formatting.None,
            };
            settings.Converters.Add(new Vector2Converter());
            settings.Converters.Add(new Vector3Converter());
            settings.Converters.Add(new Vector4Converter());
            settings.Converters.Add(new QuaternionConverter());
            settings.Converters.Add(new ColorConverter());
            settings.Converters.Add(new Color32Converter());
            settings.Converters.Add(new RectConverter());
            settings.Converters.Add(new BoundsConverter());
            settings.Converters.Add(new LayerMaskConverter());
            settings.Converters.Add(new UnityObjectConverter());
            return settings;
        }

        public static JToken ToJson(object value)
            => value == null ? JValue.CreateNull() : JToken.FromObject(value, Serializer);

        // ponytail: EntityId raw data is valid only for the current Editor process; persistent references need GlobalObjectId.
        // Unity 6000.3 exposes signed int conversions; Unity 6000.5 replaces them with ulong helpers.
        public static string ToEntityIdString(UnityEngine.Object value)
        {
#if UNITY_6000_5_OR_NEWER
            return EntityId.ToULong(value.GetEntityId()).ToString(CultureInfo.InvariantCulture);
#else
            return ((int)value.GetEntityId()).ToString(CultureInfo.InvariantCulture);
#endif
        }

        public static bool TryParseEntityId(JToken token, out EntityId entityId)
        {
            entityId = EntityId.None;
            if (token?.Type != JTokenType.String)
                return false;

#if UNITY_6000_5_OR_NEWER
            if (!ulong.TryParse((string)token, NumberStyles.None, CultureInfo.InvariantCulture, out var rawData))
                return false;
            entityId = EntityId.FromULong(rawData);
#else
            if (!int.TryParse((string)token, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var rawData))
                return false;
            entityId = (EntityId)rawData;
#endif
            return true;
        }

        private static float ReadFloat(JObject o, string key, string typeName)
        {
            var token = o[key];
            if (token == null || token.Type == JTokenType.Null)
                throw new JsonSerializationException($"{typeName} is missing required field '{key}'.");
            return token.Value<float>();
        }

        private static byte ReadByte(JObject o, string key, string typeName)
        {
            var token = o[key];
            if (token == null || token.Type == JTokenType.Null)
                throw new JsonSerializationException($"{typeName} is missing required field '{key}'.");
            return token.Value<byte>();
        }

        private static int ReadInt(JObject o, string key, string typeName)
        {
            var token = o[key];
            if (token == null || token.Type == JTokenType.Null)
                throw new JsonSerializationException($"{typeName} is missing required field '{key}'.");
            return token.Value<int>();
        }

        private sealed class Vector2Converter : JsonConverter<Vector2>
        {
            public override void WriteJson(JsonWriter w, Vector2 v, JsonSerializer s)
            {
                w.WriteStartObject();
                w.WritePropertyName("x"); w.WriteValue(v.x);
                w.WritePropertyName("y"); w.WriteValue(v.y);
                w.WriteEndObject();
            }

            public override Vector2 ReadJson(JsonReader r, Type t, Vector2 existing, bool hasExisting, JsonSerializer s)
            {
                if (r.TokenType == JsonToken.Null) return default;
                var o = JObject.Load(r);
                return new Vector2(
                    ReadFloat(o, "x", "Vector2"),
                    ReadFloat(o, "y", "Vector2"));
            }
        }

        private sealed class Vector3Converter : JsonConverter<Vector3>
        {
            public override void WriteJson(JsonWriter w, Vector3 v, JsonSerializer s)
            {
                w.WriteStartObject();
                w.WritePropertyName("x"); w.WriteValue(v.x);
                w.WritePropertyName("y"); w.WriteValue(v.y);
                w.WritePropertyName("z"); w.WriteValue(v.z);
                w.WriteEndObject();
            }

            public override Vector3 ReadJson(JsonReader r, Type t, Vector3 existing, bool hasExisting, JsonSerializer s)
            {
                if (r.TokenType == JsonToken.Null) return default;
                var o = JObject.Load(r);
                return new Vector3(
                    ReadFloat(o, "x", "Vector3"),
                    ReadFloat(o, "y", "Vector3"),
                    ReadFloat(o, "z", "Vector3"));
            }
        }

        private sealed class Vector4Converter : JsonConverter<Vector4>
        {
            public override void WriteJson(JsonWriter w, Vector4 v, JsonSerializer s)
            {
                w.WriteStartObject();
                w.WritePropertyName("x"); w.WriteValue(v.x);
                w.WritePropertyName("y"); w.WriteValue(v.y);
                w.WritePropertyName("z"); w.WriteValue(v.z);
                w.WritePropertyName("w"); w.WriteValue(v.w);
                w.WriteEndObject();
            }

            public override Vector4 ReadJson(JsonReader r, Type t, Vector4 existing, bool hasExisting, JsonSerializer s)
            {
                if (r.TokenType == JsonToken.Null) return default;
                var o = JObject.Load(r);
                return new Vector4(
                    ReadFloat(o, "x", "Vector4"),
                    ReadFloat(o, "y", "Vector4"),
                    ReadFloat(o, "z", "Vector4"),
                    ReadFloat(o, "w", "Vector4"));
            }
        }

        private sealed class QuaternionConverter : JsonConverter<Quaternion>
        {
            public override void WriteJson(JsonWriter w, Quaternion q, JsonSerializer s)
            {
                w.WriteStartObject();
                w.WritePropertyName("x"); w.WriteValue(q.x);
                w.WritePropertyName("y"); w.WriteValue(q.y);
                w.WritePropertyName("z"); w.WriteValue(q.z);
                w.WritePropertyName("w"); w.WriteValue(q.w);
                w.WriteEndObject();
            }

            public override Quaternion ReadJson(JsonReader r, Type t, Quaternion existing, bool hasExisting, JsonSerializer s)
            {
                if (r.TokenType == JsonToken.Null) return Quaternion.identity;
                var o = JObject.Load(r);
                return new Quaternion(
                    ReadFloat(o, "x", "Quaternion"),
                    ReadFloat(o, "y", "Quaternion"),
                    ReadFloat(o, "z", "Quaternion"),
                    ReadFloat(o, "w", "Quaternion"));
            }
        }

        private sealed class ColorConverter : JsonConverter<Color>
        {
            public override void WriteJson(JsonWriter w, Color c, JsonSerializer s)
            {
                w.WriteStartObject();
                w.WritePropertyName("r"); w.WriteValue(c.r);
                w.WritePropertyName("g"); w.WriteValue(c.g);
                w.WritePropertyName("b"); w.WriteValue(c.b);
                w.WritePropertyName("a"); w.WriteValue(c.a);
                w.WriteEndObject();
            }

            public override Color ReadJson(JsonReader r, Type t, Color existing, bool hasExisting, JsonSerializer s)
            {
                if (r.TokenType == JsonToken.Null) return default;
                var o = JObject.Load(r);
                return new Color(
                    ReadFloat(o, "r", "Color"),
                    ReadFloat(o, "g", "Color"),
                    ReadFloat(o, "b", "Color"),
                    ReadFloat(o, "a", "Color"));
            }
        }

        private sealed class Color32Converter : JsonConverter<Color32>
        {
            public override void WriteJson(JsonWriter w, Color32 c, JsonSerializer s)
            {
                w.WriteStartObject();
                w.WritePropertyName("r"); w.WriteValue(c.r);
                w.WritePropertyName("g"); w.WriteValue(c.g);
                w.WritePropertyName("b"); w.WriteValue(c.b);
                w.WritePropertyName("a"); w.WriteValue(c.a);
                w.WriteEndObject();
            }

            public override Color32 ReadJson(JsonReader r, Type t, Color32 existing, bool hasExisting, JsonSerializer s)
            {
                if (r.TokenType == JsonToken.Null) return default;
                var o = JObject.Load(r);
                return new Color32(
                    ReadByte(o, "r", "Color32"),
                    ReadByte(o, "g", "Color32"),
                    ReadByte(o, "b", "Color32"),
                    ReadByte(o, "a", "Color32"));
            }
        }

        private sealed class RectConverter : JsonConverter<Rect>
        {
            public override void WriteJson(JsonWriter w, Rect r, JsonSerializer s)
            {
                w.WriteStartObject();
                w.WritePropertyName("x"); w.WriteValue(r.x);
                w.WritePropertyName("y"); w.WriteValue(r.y);
                w.WritePropertyName("width"); w.WriteValue(r.width);
                w.WritePropertyName("height"); w.WriteValue(r.height);
                w.WriteEndObject();
            }

            public override Rect ReadJson(JsonReader r, Type t, Rect existing, bool hasExisting, JsonSerializer s)
            {
                if (r.TokenType == JsonToken.Null) return default;
                var o = JObject.Load(r);
                return new Rect(
                    ReadFloat(o, "x", "Rect"),
                    ReadFloat(o, "y", "Rect"),
                    ReadFloat(o, "width", "Rect"),
                    ReadFloat(o, "height", "Rect"));
            }
        }

        private sealed class BoundsConverter : JsonConverter<Bounds>
        {
            public override void WriteJson(JsonWriter w, Bounds b, JsonSerializer s)
            {
                w.WriteStartObject();
                w.WritePropertyName("center"); UnitySerializer.Serializer.Serialize(w, b.center);
                w.WritePropertyName("size"); UnitySerializer.Serializer.Serialize(w, b.size);
                w.WriteEndObject();
            }

            public override Bounds ReadJson(JsonReader r, Type t, Bounds existing, bool hasExisting, JsonSerializer s)
            {
                if (r.TokenType == JsonToken.Null) return default;
                var o = JObject.Load(r);
                var center = o["center"];
                var size = o["size"];
                if (center == null || center.Type == JTokenType.Null)
                    throw new JsonSerializationException("Bounds is missing required field 'center'.");
                if (size == null || size.Type == JTokenType.Null)
                    throw new JsonSerializationException("Bounds is missing required field 'size'.");
                return new Bounds(
                    center.ToObject<Vector3>(UnitySerializer.Serializer),
                    size.ToObject<Vector3>(UnitySerializer.Serializer));
            }
        }

        private sealed class LayerMaskConverter : JsonConverter<LayerMask>
        {
            public override void WriteJson(JsonWriter w, LayerMask m, JsonSerializer s)
            {
                w.WriteStartObject();
                w.WritePropertyName("value"); w.WriteValue(m.value);
                w.WriteEndObject();
            }

            public override LayerMask ReadJson(JsonReader r, Type t, LayerMask existing, bool hasExisting, JsonSerializer s)
            {
                if (r.TokenType == JsonToken.Null) return default;
                var o = JObject.Load(r);
                return (LayerMask)ReadInt(o, "value", "LayerMask");
            }
        }

        private sealed class UnityObjectConverter : JsonConverter<UnityEngine.Object>
        {
            public override bool CanRead => false;

            public override void WriteJson(JsonWriter w, UnityEngine.Object value, JsonSerializer s)
            {
                if (value == null)
                {
                    w.WriteNull();
                    return;
                }

                var assetPath = AssetDatabase.GetAssetPath(value);
                w.WriteStartObject();
                w.WritePropertyName("$type"); w.WriteValue("objectRef");
                w.WritePropertyName("objectType"); w.WriteValue(value.GetType().FullName);
                w.WritePropertyName("name"); w.WriteValue(value.name);
                w.WritePropertyName("entityId"); w.WriteValue(ToEntityIdString(value));
                if (!string.IsNullOrEmpty(assetPath))
                {
                    w.WritePropertyName("assetPath"); w.WriteValue(assetPath);
                    w.WritePropertyName("guid"); w.WriteValue(AssetDatabase.AssetPathToGUID(assetPath));
                    if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(value, out _, out long localId))
                    {
                        w.WritePropertyName("localId"); w.WriteValue(localId.ToString(CultureInfo.InvariantCulture));
                    }
                }
                w.WriteEndObject();
            }

            public override UnityEngine.Object ReadJson(JsonReader r, Type t, UnityEngine.Object existing, bool hasExisting, JsonSerializer s)
                => existing;
        }
    }
}
