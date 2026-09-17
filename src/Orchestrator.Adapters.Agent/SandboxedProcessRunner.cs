using System.Diagnostics;
using System.Text;
using System.Threading;

namespace Orchestrator.Adapters.Agent;

/// <summary>A request to run a single command inside the sandbox.</summary>
/// <param name="Image">The Docker image to run (e.g. <c>orchestrator/worker:latest</c>).</param>
/// <param name="Command">The command and arguments to execute inside the container.</param>
/// <param name="WorkspacePath">
/// The host directory bind-mounted read-write at <c>/work</c> inside the container; the only
/// writable location the process has.
/// </param>
/// <param name="Environment">Environment variables passed explicitly into the container.</param>
/// <param name="AllowedHosts">
/// Hosts this run is authorized to reach. Accepted for forward compatibility with
/// envelope-scoped, per-run allowlisting (a later task); Task 25's actual enforcement is the
/// static list in deploy/egress-allowlist.conf applied by the egress-proxy container - see this
/// task's report for why.
/// </param>
/// <param name="Timeout">How long the run is allowed before it is killed.</param>
/// <param name="StdinContent">Optional content written to the process's standard input.</param>
public sealed record SandboxRequest(
    string Image,
    IReadOnlyList<string> Command,
    string WorkspacePath,
    IReadOnlyDictionary<string, string> Environment,
    IReadOnlyList<string> AllowedHosts,
    TimeSpan Timeout,
    string? StdinContent);

/// <summary>
/// Runs coding-agent CLIs and, later, build/test commands inside a locked-down, disposable
/// container: read-only filesystem except the workspace mount, no host credentials, and a
/// network with no route out except an allowlisting HTTP proxy.
/// </summary>
/// <remarks>
/// This shells out to the literal <c>docker</c> command line (via
/// <see cref="SandboxOptions.DockerCommand"/>) rather than a Docker SDK, because the argument
/// list built here IS the reviewable security boundary.
/// </remarks>
public sealed class SandboxedProcessRunner(SandboxOptions options, TimeProvider timeProvider)
{
    private const string NetworkName = "orchestrator-agent";
    private const string ProxyUrl = "http://egress-proxy:3128";

    public async Task<ProcessResult> RunAsync(SandboxRequest request, CancellationToken cancellationToken)
    {
        var containerName = $"orchestrator-agent-{Guid.NewGuid():N}";
        var dockerArgs = BuildDockerRunArguments(request, containerName);
        var startInfo = CreateStartInfo(dockerArgs, redirectStdin: request.StdinContent is not null);

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

        var stdout = new BoundedOutputCollector(SandboxOptions.MaxCapturedOutputBytes);
        var stderr = new BoundedOutputCollector(SandboxOptions.MaxCapturedOutputBytes);
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.Append(e.Data + "\n"); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.Append(e.Data + "\n"); };

        using var timeoutCts = new CancellationTokenSource(request.Timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        var start = timeProvider.GetTimestamp();

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        if (request.StdinContent is not null)
        {
            await process.StandardInput.WriteAsync(request.StdinContent);
            process.StandardInput.Close();
        }

        try
        {
            await process.WaitForExitAsync(linkedCts.Token);
        }
        catch (OperationCanceledException)
        {
            // Killing the local `docker run` client process does not stop the container - dockerd
            // owns its lifecycle independently of the client. Both must be killed explicitly.
            await KillContainerAsync(containerName);
            TryKillProcessTree(process);

            var elapsedOnCancel = timeProvider.GetElapsedTime(start);
            if (cancellationToken.IsCancellationRequested)
                throw new OperationCanceledException("The sandboxed run was cancelled.", cancellationToken);

            return new ProcessResult(-1, stdout.ToString(), stderr.ToString(), elapsedOnCancel, TimedOut: true);
        }

        var elapsed = timeProvider.GetElapsedTime(start);
        return new ProcessResult(process.ExitCode, stdout.ToString(), stderr.ToString(), elapsed, TimedOut: false);
    }

    private ProcessStartInfo CreateStartInfo(IReadOnlyList<string> dockerArgs, bool redirectStdin)
    {
        var command = options.DockerCommand;
        if (command.Count == 0)
            throw new InvalidOperationException("Sandbox:DockerCommand must contain at least the executable.");

        var startInfo = new ProcessStartInfo
        {
            FileName = command[0],
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = redirectStdin,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // Built from scratch: host environment variables (including any credentials) must never
        // leak into the docker CLI's own process, let alone the container it launches. `docker
        // run` never forwards its own process environment into the container regardless, so this
        // clearing is what keeps even the CLI invocation itself credential-free.
        //
        // SystemRoot is the one exception, added back after clearing: on Windows, a process
        // started with a completely empty environment block fails outright (confirmed against
        // `wsl.exe`, which this environment's dev override launches docker through - it fails
        // with an RPC error before ever reaching docker). SystemRoot is OS plumbing, not a
        // credential, and Environment.GetEnvironmentVariable returns null (a no-op add) on Linux.
        startInfo.EnvironmentVariables.Clear();
        if (Environment.GetEnvironmentVariable("SystemRoot") is { } systemRoot)
            startInfo.EnvironmentVariables["SystemRoot"] = systemRoot;

        foreach (var arg in command.Skip(1).Concat(dockerArgs))
            startInfo.ArgumentList.Add(arg);

        return startInfo;
    }

    private static IReadOnlyList<string> BuildDockerRunArguments(SandboxRequest request, string containerName)
    {
        List<string> args =
        [
            "run", "--rm",
            "--name", containerName,
            "--network", NetworkName,
            "--read-only",
            "--tmpfs", "/tmp:rw,noexec,nosuid,size=512m",
            "-v", $"{ToContainerHostPath(request.WorkspacePath)}:/work:rw",
            "-w", "/work",
            "--user", "10001:10001",
            "--memory", "4g",
            "--cpus", "2",
            "--pids-limit", "512",
            "--cap-drop", "ALL",
            "--security-opt", "no-new-privileges",
            "-e", $"HTTP_PROXY={ProxyUrl}",
            "-e", $"HTTPS_PROXY={ProxyUrl}",
            "-e", "NO_PROXY=localhost,127.0.0.1",
        ];

        foreach (var (key, value) in request.Environment)
        {
            args.Add("-e");
            args.Add($"{key}={value}");
        }

        args.Add(request.Image);
        args.AddRange(request.Command);
        return args;
    }

    /// <summary>
    /// Translates a Windows workspace path (e.g. <c>C:\Git\...</c>) to the path the Docker daemon
    /// needs for a bind mount when it runs inside WSL2 (e.g. <c>/mnt/c/Git/...</c>). A path that
    /// is already Linux-shaped - what production always passes - is returned unchanged.
    /// </summary>
    public static string ToContainerHostPath(string path)
    {
        if (path.Length >= 2 && path[1] == ':' && char.IsAsciiLetter(path[0]))
        {
            var drive = char.ToLowerInvariant(path[0]);
            var rest = path[2..].Replace('\\', '/');
            return $"/mnt/{drive}{rest}";
        }

        return path;
    }

    private async Task KillContainerAsync(string containerName)
    {
        try
        {
            var command = options.DockerCommand;
            var killInfo = new ProcessStartInfo
            {
                FileName = command[0],
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            killInfo.EnvironmentVariables.Clear();
            if (Environment.GetEnvironmentVariable("SystemRoot") is { } systemRoot)
                killInfo.EnvironmentVariables["SystemRoot"] = systemRoot;
            foreach (var arg in command.Skip(1).Append("kill").Append(containerName))
                killInfo.ArgumentList.Add(arg);

            using var killProcess = Process.Start(killInfo);
            if (killProcess is not null)
                await killProcess.WaitForExitAsync();
        }
        catch
        {
            // Best-effort: the container may already have exited on its own.
        }
    }

    private static void TryKillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Best-effort: the process may already have exited.
        }
    }
}

/// <summary>
/// Accumulates process output up to a byte cap, appending a truncation marker once exceeded, so
/// a runaway process cannot exhaust memory capturing output no one will read in full.
/// </summary>
internal sealed class BoundedOutputCollector(int maxBytes)
{
    private const string TruncatedMarker = "\n[output truncated]";

    private readonly StringBuilder _builder = new();
    private readonly Lock _gate = new();
    private long _bytesWritten;
    private bool _truncated;

    public void Append(string text)
    {
        lock (_gate)
        {
            if (_truncated)
                return;

            var textBytes = Encoding.UTF8.GetByteCount(text);
            if (_bytesWritten + textBytes <= maxBytes)
            {
                _builder.Append(text);
                _bytesWritten += textBytes;
                return;
            }

            var remaining = (int)Math.Max(0, maxBytes - _bytesWritten);
            if (remaining > 0)
                _builder.Append(TrimToByteLength(text, remaining));

            _builder.Append(TruncatedMarker);
            _truncated = true;
        }
    }

    public override string ToString()
    {
        lock (_gate)
            return _builder.ToString();
    }

    private static string TrimToByteLength(string text, int maxBytes)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        if (bytes.Length <= maxBytes)
            return text;

        // Walk back from the byte cut point to a valid UTF-8 character boundary; ASCII output -
        // the overwhelmingly common case for CLI logs - never needs this to move at all.
        var length = maxBytes;
        while (length > 0 && (bytes[length] & 0xC0) == 0x80)
            length--;

        return Encoding.UTF8.GetString(bytes, 0, length);
    }
}
