namespace Ina.EndToEnd.Tests;

/// <summary>
/// Applications of the web SDK. This only works through <c>dotnet publish</c>: the .NET SDK resolves the shared
/// framework of ASP.NET Core, which the <c>ina</c> command does not know about.
/// </summary>
public class AspNetCorePublishTests(PublishEnvironment environment)
{
    [Fact]
    public async Task AspNetCoreApplicationServesRequests()
    {
        Assert.SkipUnless(TestEnvironment.CanRunLinuxX64, "Running the executable requires an x86-64 Linux host.");

        // The sample sends requests to itself and prints the responses.
        using var project = new SampleProject(environment, "MinimalApi", "MinimalApi.csproj", InaReference.Sdk);

        var publish = await project.PublishAsync("-r", "linux-x64");
        Assert.True(publish.ExitCode == 0, publish.Output + publish.Error);

        var native = await TestEnvironment.RunAsync(project.Executable);
        var reference = await TestEnvironment.RunOnJitAsync(TestEnvironment.SampleAssembly("MinimalApi", "MinimalApi"));

        Assert.True(native.ExitCode == 0, native.Output + native.Error);
        Assert.Contains("POST /todos -> 201 /todos/3", native.Output);
        Assert.Contains("GET /todos/99 -> 404", native.Output);
        Assert.Equal(reference.Output, native.Output);
    }
}
