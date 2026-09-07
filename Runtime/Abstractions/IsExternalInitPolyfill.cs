// Polyfill so C# 9 init-only setters compile against runtime frameworks (Unity's
// .NET Standard 2.1 reference assemblies) that do not ship IsExternalInit. The
// C# compiler only looks up the type by full name; supplying it as internal here
// is the standard workaround.
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit
    {
    }
}
