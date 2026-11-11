using System;
using Newtonsoft.Json;
using UnityEngine;

namespace Yixuan.Pick
{
    /// <summary>
    /// Newtonsoft settings for evidence JSON that may contain UnityEngine value
    /// types. Default Json.NET reflection walks Vector3.normalized (a property
    /// returning Vector3) and reports a self reference; these converters write
    /// the plain numeric fields instead.
    /// </summary>
    public static class SelectionJson
    {
        public static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
        {
            Formatting = Formatting.Indented,
            Converters = { new UnityPrimitiveConverter() },
        };

        public static string Serialize(object value)
        {
            return JsonConvert.SerializeObject(value, Settings);
        }

        public static string SerializeCompact(object value)
        {
            var settings = new JsonSerializerSettings
            {
                Formatting = Formatting.None,
                Converters = { new UnityPrimitiveConverter() },
            };
            return JsonConvert.SerializeObject(value, settings);
        }

        sealed class UnityPrimitiveConverter : JsonConverter
        {
            public override bool CanRead { get { return false; } }

            public override bool CanConvert(Type objectType)
            {
                return objectType == typeof(Vector2) || objectType == typeof(Vector3) ||
                       objectType == typeof(Vector4) || objectType == typeof(Quaternion) ||
                       objectType == typeof(Matrix4x4) || objectType == typeof(Color) ||
                       objectType == typeof(Color32) || objectType == typeof(Bounds);
            }

            public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
            {
                if (value is Vector2 v2) { writer.WriteStartObject(); writer.WritePropertyName("x"); writer.WriteValue(v2.x); writer.WritePropertyName("y"); writer.WriteValue(v2.y); writer.WriteEndObject(); return; }
                if (value is Vector3 v3) { writer.WriteStartObject(); writer.WritePropertyName("x"); writer.WriteValue(v3.x); writer.WritePropertyName("y"); writer.WriteValue(v3.y); writer.WritePropertyName("z"); writer.WriteValue(v3.z); writer.WriteEndObject(); return; }
                if (value is Vector4 v4) { writer.WriteStartObject(); writer.WritePropertyName("x"); writer.WriteValue(v4.x); writer.WritePropertyName("y"); writer.WriteValue(v4.y); writer.WritePropertyName("z"); writer.WriteValue(v4.z); writer.WritePropertyName("w"); writer.WriteValue(v4.w); writer.WriteEndObject(); return; }
                if (value is Quaternion q) { writer.WriteStartObject(); writer.WritePropertyName("x"); writer.WriteValue(q.x); writer.WritePropertyName("y"); writer.WriteValue(q.y); writer.WritePropertyName("z"); writer.WriteValue(q.z); writer.WritePropertyName("w"); writer.WriteValue(q.w); writer.WriteEndObject(); return; }
                if (value is Color c) { writer.WriteStartObject(); writer.WritePropertyName("r"); writer.WriteValue(c.r); writer.WritePropertyName("g"); writer.WriteValue(c.g); writer.WritePropertyName("b"); writer.WriteValue(c.b); writer.WritePropertyName("a"); writer.WriteValue(c.a); writer.WriteEndObject(); return; }
                if (value is Color32 c32) { writer.WriteStartObject(); writer.WritePropertyName("r"); writer.WriteValue(c32.r); writer.WritePropertyName("g"); writer.WriteValue(c32.g); writer.WritePropertyName("b"); writer.WriteValue(c32.b); writer.WritePropertyName("a"); writer.WriteValue(c32.a); writer.WriteEndObject(); return; }
                if (value is Bounds bounds)
                {
                    writer.WriteStartObject();
                    writer.WritePropertyName("center"); serializer.Serialize(writer, bounds.center);
                    writer.WritePropertyName("size"); serializer.Serialize(writer, bounds.size);
                    writer.WriteEndObject();
                    return;
                }
                if (value is Matrix4x4 matrix)
                {
                    writer.WriteStartArray();
                    for (int row = 0; row < 4; row++)
                        for (int column = 0; column < 4; column++)
                            writer.WriteValue(matrix[row, column]);
                    writer.WriteEndArray();
                    return;
                }
                writer.WriteNull();
            }

            public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
            {
                throw new NotSupportedException();
            }
        }
    }
}
