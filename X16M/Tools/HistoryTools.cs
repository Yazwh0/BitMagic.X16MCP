using System.ComponentModel;
using ModelContextProtocol.Server;
using X16M.Dap;

namespace X16M.Tools;

[McpServerToolType]
public static class HistoryTools
{
    [McpServerTool(Name = "get_cpu_history", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Returns the most recently executed CPU instructions, most recent first, with the register state and flags at each step plus source file/line where known. Useful for seeing how execution actually reached the current stop, not just where it is now.")]
    public static string GetCpuHistory(
        DapSession session,
        [Description("Number of instructions to return, most recent first. Capped at 1024, the size of one history page.")] int count = 50)
    {
        var response = session.GetHistory();
        count = Math.Clamp(count, 1, 1024);

        var lines = response.HistoryItems.Take(count).Select(i =>
        {
            var line = $"clock {i.Clock,-10} A:${i.A:x2} X:${i.X:x2} Y:${i.Y:x2} SP:${i.Sp:x2} {i.Flags} PC:${i.Pc:x4} {i.OpCode}";
            if (!string.IsNullOrEmpty(i.SourceFile))
                line += $" ({i.SourceFile}:{i.LineNumber})";
            return line;
        });

        return string.Join(Environment.NewLine, lines);
    }
}
