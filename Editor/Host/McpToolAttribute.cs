using System;

namespace LittleBrushGames.Mcp.Editor
{
    /// <summary>Declares a parameterless ToolDescriptor factory, or a sequence of related descriptors.</summary>
    [AttributeUsage(AttributeTargets.Method, Inherited = false)]
    public sealed class McpToolDeclarationAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.Method, Inherited = false)]
    public sealed class McpToolAttribute : Attribute
    {
        public string Name { get; }
        public string Description { get; }
        public ToolTrustCategory Access { get; }
        public ToolAvailability Availability { get; set; } = ToolAvailability.EditMode;
        public bool RequiresMainThread { get; set; } = true;
        public bool RequiresWriterLease { get; set; } = true;
        public bool ReloadSafe { get; set; }
        public int TimeoutMs { get; set; }
        public string ExclusiveGroup { get; set; }
        public bool DestructiveHint { get; set; }
        public bool IdempotentHint { get; set; }

        public McpToolAttribute(string name, string description, ToolTrustCategory access)
        {
            Name = name;
            Description = description;
            Access = access;
            DestructiveHint = access != ToolTrustCategory.Read;
        }
    }

    [AttributeUsage(AttributeTargets.Parameter)]
    public sealed class McpParameterAttribute : Attribute
    {
        public string Description { get; set; }
        public int MinLength { get; set; } = -1;
        public int MaxLength { get; set; } = -1;
        public int MinItems { get; set; } = -1;
        public int MaxItems { get; set; } = -1;
        public double Minimum { get; set; } = double.NaN;
        public double Maximum { get; set; } = double.NaN;
    }
}
