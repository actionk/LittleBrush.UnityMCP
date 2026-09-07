using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace LittleBrushGames.Mcp.Editor.Dispatch
{
    public readonly struct ValidationError
    {
        public string Path { get; }
        public string Message { get; }

        public ValidationError(string path, string message)
        {
            Path = path;
            Message = message;
        }
    }

    public static class SchemaValidator
    {
        private static readonly HashSet<string> s_supportedKeywords = new(StringComparer.Ordinal)
        {
            "$id", "$schema", "additionalProperties", "const", "default", "deprecated", "description",
            "enum", "examples", "exclusiveMaximum", "exclusiveMinimum", "format", "items", "maximum",
            "maxItems", "maxLength", "minimum", "minItems", "minLength", "properties", "readOnly",
            "required", "title", "type", "uniqueItems", "writeOnly",
        };
        private static readonly HashSet<string> s_supportedTypes = new(StringComparer.Ordinal)
        {
            "array", "boolean", "integer", "null", "number", "object", "string",
        };

        public static IEnumerable<ValidationError> Validate(JToken schema, JToken value)
        {
            var errors = new List<ValidationError>();
            ValidateNode(schema, value, "", errors);
            return errors;
        }

        public static IEnumerable<ValidationError> ValidateSchema(JObject schema)
        {
            var errors = new List<ValidationError>();
            ValidateSchemaNode(schema, "", errors);
            return errors;
        }

        private static void ValidateNode(JToken schema, JToken value, string path, List<ValidationError> errors)
        {
            if (schema is not JObject schemaObj) return;

            if (schemaObj["const"] is JToken constant && !JToken.DeepEquals(constant, value))
                errors.Add(new ValidationError(path, "value does not match const"));

            if (schemaObj["enum"] is JArray enumValues)
            {
                var match = false;
                foreach (var candidate in enumValues)
                {
                    if (JToken.DeepEquals(candidate, value)) { match = true; break; }
                }
                if (!match) errors.Add(new ValidationError(path, "value not in enum"));
            }

            var type = FindMatchingType(schemaObj["type"], value);
            if (!string.IsNullOrEmpty(type))
            {
                switch (type)
                {
                    case "object":
                        ValidateObject(schemaObj, (JObject)value, path, errors);
                        break;
                    case "array":
                        ValidateArray(schemaObj, (JArray)value, path, errors);
                        break;
                    case "string":
                        ValidateString(schemaObj, (string)value, path, errors);
                        break;
                    case "integer":
                    case "number":
                        ValidateNumber(schemaObj, value, path, errors);
                        break;
                }
            }
            else if (schemaObj["type"] != null)
            {
                errors.Add(new ValidationError(path, $"expected {DescribeTypes(schemaObj["type"])}, got {value?.Type.ToString().ToLowerInvariant()}"));
            }
        }

        private static string FindMatchingType(JToken typeToken, JToken value)
        {
            if (typeToken == null) return null;
            if (typeToken.Type == JTokenType.String)
            {
                var type = (string)typeToken;
                return MatchesType(type, value) ? type : null;
            }
            if (typeToken is JArray arr)
            {
                foreach (var item in arr)
                {
                    var type = (string)item;
                    if (MatchesType(type, value))
                        return type;
                }
            }
            return null;
        }

        private static string DescribeTypes(JToken typeToken)
            => typeToken is JArray arr ? string.Join("|", arr.Values<string>()) : (string)typeToken;

        private static bool MatchesType(string type, JToken value) => type switch
        {
            "object"  => value is JObject,
            "array"   => value is JArray,
            "string"  => value?.Type == JTokenType.String,
            "integer" => value?.Type == JTokenType.Integer,
            "number"  => value?.Type == JTokenType.Integer || value?.Type == JTokenType.Float,
            "boolean" => value?.Type == JTokenType.Boolean,
            "null"    => value == null || value.Type == JTokenType.Null,
            _ => true,
        };

        private static void ValidateObject(JObject schema, JObject value, string path, List<ValidationError> errors)
        {
            if (schema["required"] is JArray required)
            {
                foreach (var req in required)
                {
                    var name = (string)req;
                    if (value[name] == null)
                        errors.Add(new ValidationError($"{path}/{name}", $"required property '{name}' missing"));
                }
            }

            var properties = schema["properties"] as JObject;
            if (properties != null)
            {
                foreach (var prop in properties.Properties())
                {
                    if (value[prop.Name] is JToken child)
                        ValidateNode(prop.Value, child, $"{path}/{prop.Name}", errors);
                }
            }

            if (schema["additionalProperties"]?.Type == JTokenType.Boolean
                && schema["additionalProperties"]?.Value<bool>() == false)
            {
                foreach (var prop in value.Properties())
                    if (properties == null || properties[prop.Name] == null)
                        errors.Add(new ValidationError($"{path}/{prop.Name}", "additional property not allowed"));
            }
            else if (schema["additionalProperties"] is JObject additionalSchema)
            {
                foreach (var prop in value.Properties())
                    if (properties == null || properties[prop.Name] == null)
                        ValidateNode(additionalSchema, prop.Value, $"{path}/{prop.Name}", errors);
            }
        }

        private static void ValidateArray(JObject schema, JArray value, string path, List<ValidationError> errors)
        {
            if (schema["minItems"] != null && value.Count < (int)schema["minItems"])
                errors.Add(new ValidationError(path, $"minItems {(int)schema["minItems"]}"));
            if (schema["maxItems"] != null && value.Count > (int)schema["maxItems"])
                errors.Add(new ValidationError(path, $"maxItems {(int)schema["maxItems"]}"));
            if (schema["uniqueItems"]?.Value<bool>() == true)
            {
                for (var i = 0; i < value.Count; i++)
                for (var j = i + 1; j < value.Count; j++)
                    if (JToken.DeepEquals(value[i], value[j]))
                    {
                        errors.Add(new ValidationError($"{path}/{j}", "items must be unique"));
                        i = value.Count;
                        break;
                    }
            }

            if (schema["items"] is JObject items)
            {
                for (var i = 0; i < value.Count; i++)
                    ValidateNode(items, value[i], $"{path}/{i}", errors);
            }
        }

        private static void ValidateString(JObject schema, string value, string path, List<ValidationError> errors)
        {
            if (value == null) return;
            if (schema["minLength"] != null && value.Length < (int)schema["minLength"])
                errors.Add(new ValidationError(path, $"minLength {(int)schema["minLength"]}"));
            if (schema["maxLength"] != null && value.Length > (int)schema["maxLength"])
                errors.Add(new ValidationError(path, $"maxLength {(int)schema["maxLength"]}"));
        }

        private static void ValidateNumber(JObject schema, JToken value, string path, List<ValidationError> errors)
        {
            var asDouble = value.Value<double>();
            if (schema["minimum"] != null && asDouble < (double)schema["minimum"])
                errors.Add(new ValidationError(path, $"minimum {(double)schema["minimum"]}"));
            if (schema["maximum"] != null && asDouble > (double)schema["maximum"])
                errors.Add(new ValidationError(path, $"maximum {(double)schema["maximum"]}"));
            if (schema["exclusiveMinimum"] != null && asDouble <= (double)schema["exclusiveMinimum"])
                errors.Add(new ValidationError(path, $"exclusiveMinimum {(double)schema["exclusiveMinimum"]}"));
            if (schema["exclusiveMaximum"] != null && asDouble >= (double)schema["exclusiveMaximum"])
                errors.Add(new ValidationError(path, $"exclusiveMaximum {(double)schema["exclusiveMaximum"]}"));
        }

        private static void ValidateSchemaNode(JObject schema, string path, List<ValidationError> errors)
        {
            foreach (var property in schema.Properties())
                if (!s_supportedKeywords.Contains(property.Name))
                    errors.Add(new ValidationError(path, $"unsupported schema keyword '{property.Name}'"));

            if (schema["type"] is JToken type)
            {
                var types = type.Type == JTokenType.String
                    ? new[] { type.Value<string>() }
                    : type is JArray array ? array.Values<string>() : Enumerable.Empty<string>();
                foreach (var candidate in types)
                    if (string.IsNullOrEmpty(candidate) || !s_supportedTypes.Contains(candidate))
                        errors.Add(new ValidationError(path + "/type", $"unsupported type '{candidate}'"));
            }

            if (schema["properties"] is JObject properties)
                foreach (var property in properties.Properties())
                    if (property.Value is JObject child)
                        ValidateSchemaNode(child, path + "/properties/" + property.Name, errors);
                    else
                        errors.Add(new ValidationError(path + "/properties/" + property.Name, "property schema must be an object"));
            else if (schema["properties"] != null)
                errors.Add(new ValidationError(path + "/properties", "properties must be an object"));

            if (schema["items"] is JObject items)
                ValidateSchemaNode(items, path + "/items", errors);
            else if (schema["items"] != null)
                errors.Add(new ValidationError(path + "/items", "items must be an object"));

            if (schema["additionalProperties"] is JObject additional)
                ValidateSchemaNode(additional, path + "/additionalProperties", errors);
        }
    }
}
