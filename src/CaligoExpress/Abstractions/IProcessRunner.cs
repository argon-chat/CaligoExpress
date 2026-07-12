namespace CaligoExpress.Abstractions;

using System.Diagnostics;

/// <summary>How to start a child process (the relaunched bootstrapper, or the main app).</summary>
public sealed record ProcessStartSpec(
    string FileName,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory = null,
    bool CreateNoWindow = false);

/// <summary>Injected process launcher so the self-update/relaunch path is testable without spawning anything.</summary>
public interface IProcessRunner
{
    int Start(ProcessStartSpec spec);
}

/// <summary>Real launcher. (The installer adds de-elevation on top of this when launching the app.)</summary>
public sealed class SystemProcessRunner : IProcessRunner
{
    public static readonly SystemProcessRunner Instance = new();

    public int Start(ProcessStartSpec spec)
    {
        var psi = new ProcessStartInfo
        {
            FileName = spec.FileName,
            WorkingDirectory = spec.WorkingDirectory ?? Path.GetDirectoryName(spec.FileName),
            UseShellExecute = false,
            CreateNoWindow = spec.CreateNoWindow,
        };
        foreach (var arg in spec.Arguments)
            psi.ArgumentList.Add(arg);

        var process = Process.Start(psi);
        return process?.Id ?? -1;
    }
}
