using System.Diagnostics;

namespace Ina.Linker.Tests.Support;

/// <summary>Runs linked executables for the tests.</summary>
internal static class TestProcess
{
    /// <summary>Runs a process to completion and returns its exit code and its output (standard output, then standard error).</summary>
    public static (int ExitCode, string Output) Run(ProcessStartInfo startInfo)
    {
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.UseShellExecute = false;

        using var process = StartWithRetry(startInfo);
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(TimeSpan.FromSeconds(30)))
        {
            process.Kill();
            Assert.Fail("The linked executable did not exit in time.");
        }

        return (process.ExitCode, output + error);
    }

    /// <summary>
    /// Starts a process, retrying while the executable is "busy". Tests run in parallel, and a process forked by
    /// another test briefly inherits the handle through which this test wrote its executable; the kernel refuses to
    /// execute a file that is open for writing anywhere (ETXTBSY).
    /// </summary>
    private static Process StartWithRetry(ProcessStartInfo startInfo)
    {
        const int TextFileBusy = 26;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return Process.Start(startInfo)!;
            }
            catch (System.ComponentModel.Win32Exception e) when (e.NativeErrorCode == TextFileBusy && attempt < 50)
            {
                Thread.Sleep(20);
            }
        }
    }
}
