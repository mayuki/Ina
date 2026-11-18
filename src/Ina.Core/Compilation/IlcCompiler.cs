using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Ina.Compilation;

/// <summary>Runs ILCompiler, which turns IL into a relocatable object file for the target platform.</summary>
public sealed class IlcCompiler(ILogger logger)
{
    public async Task CompileAsync(string compilerPath, IlcOptions options, string responseFilePath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(compilerPath))
        {
            throw new InaException($"ILCompiler was not found at {compilerPath}.");
        }

        await File.WriteAllLinesAsync(responseFilePath, IlcArguments.ToResponseFileLines(IlcArguments.Build(options)), cancellationToken).ConfigureAwait(false);
        logger.LogDebug("Running {Compiler} @{ResponseFile}", compilerPath, responseFilePath);

        var startInfo = new ProcessStartInfo(compilerPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add($"@{responseFilePath}");

        using var process = new Process { StartInfo = startInfo };
        var errors = new List<string>();
        process.OutputDataReceived += (_, e) => Report(e.Data, errors);
        process.ErrorDataReceived += (_, e) => Report(e.Data, errors);

        try
        {
            process.Start();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new InaException($"Could not start ILCompiler ({compilerPath}): {e.Message}", e);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        if (process.ExitCode != 0)
        {
            var details = errors.Count > 0 ? Environment.NewLine + string.Join(Environment.NewLine, errors.TakeLast(20)) : string.Empty;
            throw new InaException($"ILCompiler failed with exit code {process.ExitCode}.{details}");
        }
    }

    /// <summary>Classifies a line of compiler output; ILCompiler uses the MSBuild "file: warning CODE: text" format.</summary>
    private void Report(string? line, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        if (line.Contains(": error ", StringComparison.Ordinal) || line.StartsWith("error", StringComparison.OrdinalIgnoreCase))
        {
            lock (errors)
            {
                errors.Add(line);
            }

            logger.LogError("{Line}", line);
        }
        else if (line.Contains(": warning ", StringComparison.Ordinal))
        {
            logger.LogWarning("{Line}", line);
        }
        else
        {
            lock (errors)
            {
                errors.Add(line);
            }

            logger.LogDebug("{Line}", line);
        }
    }
}
