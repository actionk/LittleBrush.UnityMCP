using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace LittleBrushGames.Mcp.Editor.Dispatch
{
    public interface IToolAuthorizer
    {
        ValueTask AuthorizeAsync(ToolDescriptor tool, JObject arguments, CancellationToken ct);
    }
}
