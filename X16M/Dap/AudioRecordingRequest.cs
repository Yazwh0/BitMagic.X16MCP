using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;

namespace X16M.Dap;

/// <summary>
/// Client-side mirror of X16D's "startAudioRecording" custom DAP request
/// (BitMagic.X16Debugger/CustomMessage/AudioRecording.cs). Starts recording the emulator's audio
/// output to a WAV file, taken from the emulator's own output buffer - so it's unaffected by the
/// window being muted, and nothing is written while the emulator is stopped.
/// </summary>
internal sealed class StartAudioRecordingRequest : DebugRequestWithResponse<StartAudioRecordingRequestArguments, StartAudioRecordingRequestResponse>
{
    public StartAudioRecordingRequest() : base("startAudioRecording")
    {
    }
}

internal sealed class StartAudioRecordingRequestArguments : DebugRequestArguments
{
    public string Path { get; set; } = "";
}

public sealed class StartAudioRecordingRequestResponse : ResponseBody
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string? Path { get; set; }
}

/// <summary>
/// Client-side mirror of X16D's "stopAudioRecording" custom DAP request. Stops the recording
/// started by "startAudioRecording" and finalises the WAV file.
/// </summary>
internal sealed class StopAudioRecordingRequest : DebugRequestWithResponse<StopAudioRecordingRequestArguments, StopAudioRecordingRequestResponse>
{
    public StopAudioRecordingRequest() : base("stopAudioRecording")
    {
    }
}

internal sealed class StopAudioRecordingRequestArguments : DebugRequestArguments
{
}

public sealed class StopAudioRecordingRequestResponse : ResponseBody
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string? Path { get; set; }
    public long Frames { get; set; }
    public double Seconds { get; set; }
}
