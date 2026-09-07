using System.Linq;
using LittleBrushGames.Mcp.Editor.Dispatch;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace LittleBrushGames.Mcp.Tests.Dispatch
{
    public class SchemaValidatorTests
    {
        [Test]
        public void Validate_Object_RequiredMissing_ReturnsError()
        {
            var schema = JObject.Parse(@"{
                ""type"": ""object"",
                ""properties"": { ""name"": { ""type"": ""string"" } },
                ""required"": [""name""]
            }");
            var value = JObject.Parse(@"{}");
            var errors = SchemaValidator.Validate(schema, value).ToList();
            Assert.That(errors, Has.Count.EqualTo(1));
            Assert.That(errors[0].Path, Is.EqualTo("/name"));
        }

        [Test]
        public void Validate_WrongType_ReturnsError()
        {
            var schema = JObject.Parse(@"{ ""type"": ""integer"" }");
            var value = JValue.CreateString("hello");
            var errors = SchemaValidator.Validate(schema, value).ToList();
            Assert.That(errors, Has.Count.EqualTo(1));
            Assert.That(errors[0].Message, Does.Contain("integer"));
        }

        [Test]
        public void Validate_EnumMismatch_ReturnsError()
        {
            var schema = JObject.Parse(@"{ ""enum"": [""a"", ""b""] }");
            var errors = SchemaValidator.Validate(schema, JValue.CreateString("c")).ToList();
            Assert.That(errors, Has.Count.EqualTo(1));
        }

        [Test]
        public void Validate_IntegerBounds_ReturnsError()
        {
            var schema = JObject.Parse(@"{ ""type"": ""integer"", ""minimum"": 1, ""maximum"": 10 }");
            var errors = SchemaValidator.Validate(schema, new JValue(42)).ToList();
            Assert.That(errors, Has.Count.EqualTo(1));
            Assert.That(errors[0].Message, Does.Contain("maximum"));
        }

        [Test]
        public void Validate_ValidObject_ReturnsNoErrors()
        {
            var schema = JObject.Parse(@"{
                ""type"": ""object"",
                ""properties"": {
                    ""name"": { ""type"": ""string"" },
                    ""count"": { ""type"": ""integer"", ""minimum"": 0 }
                },
                ""required"": [""name""]
            }");
            var value = JObject.Parse(@"{ ""name"": ""abc"", ""count"": 5 }");
            var errors = SchemaValidator.Validate(schema, value).ToList();
            Assert.That(errors, Is.Empty);
        }

        [Test]
        public void Validate_ArrayMinItems_ReturnsError()
        {
            var schema = JObject.Parse(@"{ ""type"": ""array"", ""minItems"": 1 }");
            var errors = SchemaValidator.Validate(schema, new JArray()).ToList();
            Assert.That(errors, Has.Count.EqualTo(1));
            Assert.That(errors[0].Message, Does.Contain("minItems"));
        }

        [Test]
        public void Validate_AdditionalPropertiesFalse_ReturnsError()
        {
            var schema = JObject.Parse(@"{
                ""type"": ""object"",
                ""properties"": { ""name"": { ""type"": ""string"" } },
                ""additionalProperties"": false
            }");
            var errors = SchemaValidator.Validate(schema, JObject.Parse(@"{ ""name"": ""ok"", ""extra"": true }")).ToList();
            Assert.That(errors, Has.Count.EqualTo(1));
            Assert.That(errors[0].Path, Is.EqualTo("/extra"));
        }

        [Test]
        public void Validate_TypeArray_AllowsNull()
        {
            var schema = JObject.Parse(@"{ ""type"": [""string"", ""null""] }");
            var errors = SchemaValidator.Validate(schema, JValue.CreateNull()).ToList();
            Assert.That(errors, Is.Empty);
        }

        [Test]
        public void Validate_ConstUniqueItemsAndExclusiveBounds_AreEnforced()
        {
            var schema = JObject.Parse(@"{
                ""type"": ""object"",
                ""properties"": {
                    ""confirm"": { ""type"": ""boolean"", ""const"": true },
                    ""values"": { ""type"": ""array"", ""uniqueItems"": true },
                    ""ratio"": { ""type"": ""number"", ""exclusiveMinimum"": 0, ""exclusiveMaximum"": 1 }
                }
            }");
            var errors = SchemaValidator.Validate(schema, JObject.Parse(@"{
                ""confirm"": false,
                ""values"": [""same"", ""same""],
                ""ratio"": 1
            }")).ToList();

            Assert.That(errors.Select(error => error.Message), Has.Some.Contains("const"));
            Assert.That(errors.Select(error => error.Message), Has.Some.Contains("unique"));
            Assert.That(errors.Select(error => error.Message), Has.Some.Contains("exclusiveMaximum"));
        }

        [Test]
        public void ValidateSchema_RejectsUnsupportedKeywords()
        {
            var errors = SchemaValidator.ValidateSchema(JObject.Parse(@"{
                ""type"": ""object"",
                ""properties"": { ""value"": { ""type"": ""string"", ""pattern"": ""x"" } }
            }")).ToList();

            Assert.That(errors, Has.Count.EqualTo(1));
            Assert.That(errors[0].Path, Is.EqualTo("/properties/value"));
            Assert.That(errors[0].Message, Does.Contain("pattern"));
        }
    }
}
