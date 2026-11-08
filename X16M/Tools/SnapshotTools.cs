using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using X16M.Dap;

namespace X16M.Tools;

[McpServerToolType]
public static class SnapshotTools
{
    private const string NotIncluded = "Snapshots don't include the SD card, the modem or the YM2151.";

    [McpServerTool(Name = "take_snapshot", ReadOnly = false, Destructive = false, Idempotent = false)]
    [Description("Takes a snapshot of the machine (CPU, RAM, banked RAM, VERA including the screen, the other chips, the call stack and the instruction history) that restore_snapshot can go back to later. Works while running, taken at the next instruction and the target carries on, as well as while paused. Snapshots are kept by the debugger for this session only and are shared with the VSCode Frame Control view. " + NotIncluded + " Available even when attached to a session you don't own.")]
    public static async Task<string> TakeSnapshot(DapSession session)
    {
        var response = await session.TakeSnapshot();

        return response.Success && response.Snapshot != null
            ? $"Took snapshot {Describe(response.Snapshot)}."
            : $"Failed to take a snapshot: {response.Error}";
    }

    [McpServerTool(Name = "list_snapshots", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Lists the snapshots taken in this session, oldest first, with their id, name, when they were taken, the frame and the beam position. Use get_snapshot to see one's screen.")]
    public static async Task<string> ListSnapshots(DapSession session)
    {
        var response = await session.GetSnapshots();

        if (response.Snapshots.Count == 0)
            return "No snapshots have been taken in this session.";

        return string.Join(Environment.NewLine, response.Snapshots.Select(Describe));
    }

    [McpServerTool(Name = "get_snapshot", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Returns a snapshot's details and a 200x131 picture of the screen when it was taken, covering the full 800x525 beam area: the visible 640x480 top left, the blanking area dimmed, and a red cross where the beam was.")]
    public static async Task<CallToolResult> GetSnapshot(
        DapSession session,
        [Description("The snapshot's id, from take_snapshot or list_snapshots.")] int id)
    {
        var response = await session.GetSnapshots();
        var snapshot = response.Snapshots.FirstOrDefault(i => i.Id == id);
        var result = new CallToolResult();

        if (snapshot == null)
        {
            result.IsError = true;
            result.Content.Add(new TextContentBlock { Text = $"Snapshot {id} doesn't exist." });
            return result;
        }

        result.Content.Add(new TextContentBlock { Text = Describe(snapshot) });
        result.Content.Add(ImageContentBlock.FromBytes(Convert.FromBase64String(snapshot.Thumbnail), "image/png"));

        return result;
    }

    [McpServerTool(Name = "restore_snapshot", ReadOnly = false, Destructive = true, Idempotent = false)]
    [Description("Puts the machine back to a snapshot and waits for the stop that follows, as if it had been stepped there. If the target is running it's paused first. Breakpoints aren't part of a snapshot, whatever's set now is kept. " + NotIncluded + " Anything written to the SD card since isn't undone, and the modem and any connection carry on as they are. Not available while attached to a session you don't own.")]
    public static async Task<string> RestoreSnapshot(
        DapSession session,
        [Description("The snapshot's id, from take_snapshot or list_snapshots.")] int id)
    {
        var (response, stop) = await session.RestoreSnapshot(id);

        if (!response.Success || stop == null)
            return $"Failed to restore the snapshot: {response.Error}";

        var toReturn = $"Restored snapshot {(response.Snapshot != null ? Describe(response.Snapshot) : id.ToString())}. {ExecutionTools.Describe(stop)}";

        if (response.ModemActive)
            toReturn += " The modem is running and isn't part of the snapshot, so it and any connection it has may not match the restored program.";

        return toReturn;
    }

    [McpServerTool(Name = "rename_snapshot", ReadOnly = false, Destructive = false, Idempotent = true)]
    [Description("Renames a snapshot.")]
    public static async Task<string> RenameSnapshot(
        DapSession session,
        [Description("The snapshot's id.")] int id,
        [Description("The new name.")] string name)
    {
        var response = await session.RenameSnapshot(id, name);

        return response.Success ? $"Renamed snapshot {id} to {name}." : $"Snapshot {id} doesn't exist.";
    }

    [McpServerTool(Name = "delete_snapshot", ReadOnly = false, Destructive = true, Idempotent = true)]
    [Description("Deletes a snapshot, freeing its memory (about 17MB each).")]
    public static async Task<string> DeleteSnapshot(
        DapSession session,
        [Description("The snapshot's id.")] int id)
    {
        var response = await session.DeleteSnapshot(id);

        return response.Success ? $"Deleted snapshot {id}." : $"Snapshot {id} doesn't exist.";
    }

    private static string Describe(SnapshotInfo snapshot) =>
        $"{snapshot.Id}: {snapshot.Name} (taken {snapshot.Created}, frame {snapshot.Frame}, beam {snapshot.BeamX}, {snapshot.BeamY})";
}
