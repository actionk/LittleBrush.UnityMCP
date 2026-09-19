using System;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace LittleBrushGames.Mcp.Editor
{
    /// <summary>Opt-in typed methods use the same descriptors, dispatcher and trust checks as manual tools.</summary>
    public abstract class AttributedToolProvider : IToolProvider
    {
        public abstract string Namespace { get; }

        public void RegisterTools(IToolRegistration registration)
        {
            foreach (var method in GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                         | BindingFlags.Instance | BindingFlags.Static).OrderBy(m => m.Name, StringComparer.Ordinal))
            {
                var attribute = method.GetCustomAttribute<McpToolAttribute>();
                if (attribute != null) registration.Register(CreateDescriptor(method, attribute));
            }
        }

        private ToolDescriptor CreateDescriptor(MethodInfo method, McpToolAttribute attribute)
        {
            if (method.ContainsGenericParameters || method.IsAbstract || method.IsSpecialName)
                throw new InvalidOperationException($"{method.Name}: MCP methods must be concrete, non-generic methods.");
            if (attribute.Access == ToolTrustCategory.Auto || !Enum.IsDefined(typeof(ToolTrustCategory), attribute.Access)
                || string.IsNullOrWhiteSpace(attribute.Description) || attribute.TimeoutMs < 0)
                throw new InvalidOperationException($"{method.Name}: declare explicit access, a description and a nonnegative timeout.");
            var returnType = method.ReturnType;
            bool isAsync = returnType == typeof(Task<ToolResult>) || returnType == typeof(ValueTask<ToolResult>)
                || returnType == typeof(Task<JObject>) || returnType == typeof(ValueTask<JObject>);
            if (!isAsync && returnType != typeof(ToolResult) && returnType != typeof(JObject))
                throw new InvalidOperationException($"{method.Name}: return ToolResult or JObject, optionally wrapped in Task/ValueTask.");
            var parameters = method.GetParameters();
            var properties = new JObject();
            var required = new JArray();
            foreach (var parameter in parameters)
            {
                if (parameter.ParameterType == typeof(ToolContext) || parameter.ParameterType == typeof(CancellationToken))
                {
                    if (parameter.GetCustomAttribute<McpParameterAttribute>() != null)
                        throw new InvalidOperationException($"{method.Name}: injected parameters cannot have schema constraints.");
                    continue;
                }
                var schema = ParameterSchema(parameter);
                properties[parameter.Name] = schema;
                if (!parameter.HasDefaultValue) required.Add(parameter.Name);
            }
            var inputSchema = new JObject { ["type"] = "object", ["additionalProperties"] = false,
                ["properties"] = properties, ["required"] = required };
            return new ToolDescriptor
            {
                Name = attribute.Name, Description = attribute.Description, InputSchema = inputSchema,
                Availability = attribute.Availability, TrustCategory = attribute.Access,
                RequiresMainThread = attribute.RequiresMainThread, RequiresWriterLease = attribute.RequiresWriterLease,
                ReloadSafe = attribute.ReloadSafe, ExclusiveGroup = attribute.ExclusiveGroup,
                Timeout = attribute.TimeoutMs == 0 ? null : TimeSpan.FromMilliseconds(attribute.TimeoutMs),
                Execution = isAsync ? ToolExecution.Async : ToolExecution.Sync,
                Annotations = new JObject { ["readOnlyHint"] = attribute.Access == ToolTrustCategory.Read,
                    ["destructiveHint"] = attribute.DestructiveHint, ["idempotentHint"] = attribute.IdempotentHint },
                Handler = (context, cancellation) => Invoke(method, parameters, inputSchema, context, cancellation),
            };
        }

        private static JObject ParameterSchema(ParameterInfo parameter)
        {
            var schema = TypeSchema(parameter.ParameterType);
            var constraints = parameter.GetCustomAttribute<McpParameterAttribute>();
            if (constraints != null)
            {
                if (constraints.Description != null) schema["description"] = constraints.Description;
                AddBound("minLength", constraints.MinLength, "string");
                AddBound("maxLength", constraints.MaxLength, "string");
                AddBound("minItems", constraints.MinItems, "array");
                AddBound("maxItems", constraints.MaxItems, "array");
                AddNumberBound("minimum", constraints.Minimum);
                AddNumberBound("maximum", constraints.Maximum);
            }
            foreach (var pair in new[] { ("minLength", "maxLength"), ("minItems", "maxItems"), ("minimum", "maximum") })
                if (schema[pair.Item1] != null && schema[pair.Item2] != null
                    && (double)schema[pair.Item1] > (double)schema[pair.Item2])
                    throw new InvalidOperationException($"{parameter.Name}: minimum exceeds maximum.");
            if (parameter.HasDefaultValue)
            {
                var value = parameter.DefaultValue;
                var type = Nullable.GetUnderlyingType(parameter.ParameterType) ?? parameter.ParameterType;
                schema["default"] = value == null ? JValue.CreateNull()
                    : type.IsEnum ? new JValue(Enum.GetName(type, value)) : JToken.FromObject(value);
                if (value == null) AllowNull(schema);
                var errors = Dispatch.SchemaValidator.Validate(schema, schema["default"]).ToArray();
                if (errors.Length > 0) throw new InvalidOperationException($"{parameter.Name}: default violates its parameter schema.");
            }
            return schema;

            void AddBound(string key, int value, string type)
            {
                if (value == -1) return;
                if (value < 0 || !HasType(schema, type)) throw new InvalidOperationException($"{parameter.Name}: invalid {key} constraint.");
                schema[key] = value;
            }
            void AddNumberBound(string key, double value)
            {
                if (double.IsNaN(value)) return;
                if (double.IsInfinity(value) || !(HasType(schema, "integer") || HasType(schema, "number")))
                    throw new InvalidOperationException($"{parameter.Name}: invalid {key} constraint.");
                schema[key] = value;
            }
        }

        private static JObject TypeSchema(Type type)
        {
            var nullable = Nullable.GetUnderlyingType(type);
            if (nullable != null) { var schema = TypeSchema(nullable); AllowNull(schema); return schema; }
            if (type == typeof(string)) return new JObject { ["type"] = "string", ["maxLength"] = 2000 };
            if (type == typeof(bool)) return new JObject { ["type"] = "boolean" };
            if (type == typeof(int) || type == typeof(long)) return new JObject { ["type"] = "integer" };
            if (type == typeof(float) || type == typeof(double)) return new JObject { ["type"] = "number" };
            if (type.IsEnum && !type.IsDefined(typeof(FlagsAttribute), false))
                return new JObject { ["type"] = "string", ["enum"] = new JArray(Enum.GetNames(type)) };
            if (type.IsArray && type.GetArrayRank() == 1)
                return new JObject { ["type"] = "array", ["maxItems"] = 1000, ["items"] = TypeSchema(type.GetElementType()) };
            throw new InvalidOperationException($"Unsupported MCP parameter type {type}. Use manual registration for complex contracts.");
        }

        private static bool HasType(JObject schema, string name) => schema["type"] is JArray types
            ? types.Values<string>().Contains(name) : (string)schema["type"] == name;

        private static void AllowNull(JObject schema)
        {
            if (HasType(schema, "null")) return;
            schema["type"] = new JArray(schema["type"].DeepClone(), "null");
            if (schema["enum"] is JArray values) values.Add(JValue.CreateNull());
        }

        private async ValueTask<ToolResult> Invoke(MethodInfo method, ParameterInfo[] parameters, JObject schema,
            ToolContext context, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            var arguments = context.Arguments ?? new JObject();
            var errors = Dispatch.SchemaValidator.Validate(schema, arguments).Take(5).ToArray();
            if (errors.Length > 0) throw new McpToolException(McpErrorCodes.InvalidParams,
                string.Join("; ", errors.Select(e => $"{e.Path}: {e.Message}")));
            var values = new object[parameters.Length];
            for (var i = 0; i < parameters.Length; i++)
            {
                var parameter = parameters[i];
                if (parameter.ParameterType == typeof(ToolContext)) values[i] = context;
                else if (parameter.ParameterType == typeof(CancellationToken)) values[i] = cancellation;
                else if (!arguments.TryGetValue(parameter.Name, out var value)) values[i] = parameter.DefaultValue;
                else
                {
                    try { values[i] = ConvertValue(value, parameter.ParameterType); }
                    catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException or Newtonsoft.Json.JsonException)
                    { throw new McpToolException(McpErrorCodes.InvalidParams, $"{parameter.Name}: value cannot be converted to {parameter.ParameterType.Name}."); }
                }
            }
            object result;
            try { result = method.Invoke(method.IsStatic ? null : this, values); }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            { ExceptionDispatchInfo.Capture(ex.InnerException).Throw(); throw; }
            return result switch
            {
                ToolResult direct => direct,
                JObject json => ToolResult.Ok(json),
                Task<ToolResult> task => await task,
                ValueTask<ToolResult> task => await task,
                Task<JObject> task => ToolResult.Ok(await task),
                ValueTask<JObject> task => ToolResult.Ok(await task),
                _ => throw new InvalidOperationException($"{method.Name} returned no MCP result."),
            };
        }

        private static object ConvertValue(JToken value, Type type)
        {
            if (value.Type == JTokenType.Null) return null;
            type = Nullable.GetUnderlyingType(type) ?? type;
            if (type.IsArray)
            {
                var elementType = type.GetElementType();
                var array = Array.CreateInstance(elementType, ((JArray)value).Count);
                for (var i = 0; i < array.Length; i++) array.SetValue(ConvertValue(value[i], elementType), i);
                return array;
            }
            var result = value.ToObject(type);
            if (result is double d && (double.IsNaN(d) || double.IsInfinity(d))
                || result is float f && (float.IsNaN(f) || float.IsInfinity(f)))
                throw new ArgumentException("Numbers must be finite.");
            return result;
        }
    }
}
