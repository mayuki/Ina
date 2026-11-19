using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.HttpResults;

// An ASP.NET Core application that sends a few requests to itself and prints the responses. The output is
// deterministic, so it can be compared between the JIT and the native executable like that of the other samples.
var builder = WebApplication.CreateSlimBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:0");
builder.Logging.ClearProviders();
builder.Services.ConfigureHttpJsonOptions(static options => options.SerializerOptions.TypeInfoResolverChain.Insert(0, TodoJsonContext.Default));

var todos = new List<Todo> { new(1, "Walk the dog", false), new(2, "Do the dishes", true) };

var app = builder.Build();
app.MapGet("/todos", () => todos);
app.MapGet("/todos/{id}", Results<Ok<Todo>, NotFound> (int id) =>
    todos.Find(todo => todo.Id == id) is { } todo ? TypedResults.Ok(todo) : TypedResults.NotFound());
app.MapPost("/todos", (Todo todo) =>
{
    todos.Add(todo);
    return TypedResults.Created($"/todos/{todo.Id}", todo);
});

// Port 0 lets the system choose a free port; the server reports the address it is actually bound to.
await app.StartAsync();
using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };

Console.WriteLine($"GET /todos -> {await client.GetStringAsync("/todos")}");
using var created = await client.PostAsJsonAsync("/todos", new Todo(3, "Publish with Ina", false), TodoJsonContext.Default.Todo);
Console.WriteLine($"POST /todos -> {(int)created.StatusCode} {created.Headers.Location}");
Console.WriteLine($"GET /todos/3 -> {await client.GetStringAsync("/todos/3")}");
using var missing = await client.GetAsync("/todos/99");
Console.WriteLine($"GET /todos/99 -> {(int)missing.StatusCode}");

await app.StopAsync();

internal sealed record Todo(int Id, string Title, bool IsComplete);

[JsonSerializable(typeof(Todo))]
[JsonSerializable(typeof(List<Todo>))]
internal sealed partial class TodoJsonContext : JsonSerializerContext;
