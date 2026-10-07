using System.ComponentModel;
using ModelContextProtocol.Server;
using X16M.Dap;

namespace X16M.Tools;

[McpServerToolType]
public static class AudioTools
{
    [McpServerTool(Name = "start_audio_recording", ReadOnly = false, Destructive = false, Idempotent = false)]
    [Description("Starts recording the running target's audio output (VERA PSG/PCM and YM2151 mix) to a 16-bit stereo WAV file at 48828Hz, overwriting any existing file. The recording is taken straight from the emulator's output buffer: it captures the real audio even when the window is muted, and only covers time the emulator is actually running - while stopped at a breakpoint or paused nothing is written, so the file plays back without the gaps. Only one recording at a time; call stop_audio_recording to finish and finalise the file (it is also stopped automatically when the session ends). Available even when attached to a session you don't own.")]
    public static async Task<string> StartAudioRecording(
        DapSession session,
        [Description("WAV file to write. Use an absolute path; a relative path is only accepted when this session launched the project, and is resolved against the project's base path. Missing directories are created.")] string path)
    {
        var response = await session.StartAudioRecording(path);

        return response.Success
            ? $"Recording audio to {response.Path}."
            : $"Failed to start audio recording: {response.Error}";
    }

    [McpServerTool(Name = "stop_audio_recording", ReadOnly = false, Destructive = false, Idempotent = false)]
    [Description("Stops the audio recording started by start_audio_recording and finalises the WAV file. Reports the file path and the length recorded (emulated running time only).")]
    public static async Task<string> StopAudioRecording(DapSession session)
    {
        var response = await session.StopAudioRecording();

        if (response.Success)
            return $"Stopped audio recording: {response.Path}, {response.Seconds:0.00}s ({response.Frames} frames).";

        return response.Path == null
            ? $"Failed to stop audio recording: {response.Error}"
            : $"{response.Error} Saved {response.Path}, {response.Seconds:0.00}s ({response.Frames} frames).";
    }
}
