using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;

namespace X16M.Dap;

// Client-side mirrors of X16D's snapshot custom DAP requests
// (BitMagic.X16Debugger/CustomMessage/Snapshots.cs). Snapshots are held by X16D for the session.

internal sealed class TakeSnapshotRequest : DebugRequestWithResponse<TakeSnapshotRequestArguments, SnapshotRequestResponse>
{
    public TakeSnapshotRequest() : base("takeSnapshot")
    {
    }
}

internal sealed class GetSnapshotsRequest : DebugRequestWithResponse<GetSnapshotsRequestArguments, GetSnapshotsRequestResponse>
{
    public GetSnapshotsRequest() : base("getSnapshots")
    {
    }
}

internal sealed class RestoreSnapshotRequest : DebugRequestWithResponse<SnapshotIdRequestArguments, SnapshotRequestResponse>
{
    public RestoreSnapshotRequest() : base("restoreSnapshot")
    {
    }
}

internal sealed class DeleteSnapshotRequest : DebugRequestWithResponse<SnapshotIdRequestArguments, SnapshotRequestResponse>
{
    public DeleteSnapshotRequest() : base("deleteSnapshot")
    {
    }
}

internal sealed class RenameSnapshotRequest : DebugRequestWithResponse<RenameSnapshotRequestArguments, SnapshotRequestResponse>
{
    public RenameSnapshotRequest() : base("renameSnapshot")
    {
    }
}

internal sealed class TakeSnapshotRequestArguments : DebugRequestArguments
{
}

internal sealed class GetSnapshotsRequestArguments : DebugRequestArguments
{
}

internal sealed class SnapshotIdRequestArguments : DebugRequestArguments
{
    public int Id { get; set; }
}

internal sealed class RenameSnapshotRequestArguments : DebugRequestArguments
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

public sealed class SnapshotRequestResponse : ResponseBody
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public SnapshotInfo? Snapshot { get; set; }
    public bool ModemActive { get; set; }
    public bool Running { get; set; }
}

public sealed class GetSnapshotsRequestResponse : ResponseBody
{
    public List<SnapshotInfo> Snapshots { get; set; } = new();
}

public sealed class SnapshotInfo
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Created { get; set; } = "";
    public uint Frame { get; set; }
    public int BeamX { get; set; }
    public int BeamY { get; set; }
    public string Thumbnail { get; set; } = ""; // png, base64
}
