using System.ComponentModel;
using ModelContextProtocol.Server;
using X16M.Dap;

namespace X16M.Tools;

[McpServerToolType]
public static class InspectionTools
{
    [McpServerTool(Name = "get_stack_trace", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Returns the current call stack for a thread, with source file/line for each frame where known.")]
    public static string GetStackTrace(DapSession session, int threadId = 1, int startFrame = 0, int levels = 20)
    {
        var response = session.GetStackTrace(threadId, startFrame, levels);

        var frames = response.StackFrames.Select(f =>
            $"#{f.Id} {f.Name} — {(f.Source is null ? "<no source>" : f.Source.Path)}:{f.Line}");

        return string.Join(Environment.NewLine, frames);
    }

    [McpServerTool(Name = "evaluate", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Evaluates an expression in the debugger's expression language (registers, symbols, memory, etc.) in the context of a stack frame.")]
    public static string Evaluate(
        DapSession session,
        [Description("Expression to evaluate.")] string expression,
        [Description("Stack frame id to evaluate in (from get_stack_trace); omit for the top frame's default context.")] int? frameId = null)
    {
        var response = session.Evaluate(expression, frameId);
        return response.Result;
    }

    [McpServerTool(Name = "disassemble", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Disassembles instructions starting at a memory reference.")]
    public static string Disassemble(
        DapSession session,
        [Description("Memory reference to start disassembling from, e.g. an address like '0x0810' or an expression the target understands.")] string memoryReference,
        [Description("Number of instructions to disassemble.")] int instructionCount = 20)
    {
        var response = session.Disassemble(memoryReference, instructionCount);

        var lines = response.Instructions.Select(i =>
            $"{i.Address}  {i.InstructionBytes,-12} {i.Instruction}");

        return string.Join(Environment.NewLine, lines);
    }

    [McpServerTool(Name = "read_memory", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Reads raw bytes starting at a memory reference, returned as base64 (per DAP's readMemory response).")]
    public static string ReadMemory(
        DapSession session,
        [Description("Memory reference to start reading from, e.g. an address like '0x0810'.")] string memoryReference,
        [Description("Number of bytes to read.")] int count)
    {
        var response = session.ReadMemory(memoryReference, count);
        return $"address: {response.Address}, data (base64): {response.Data}";
    }
}
