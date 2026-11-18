using System.Text.Json;
using JsonReader.Models;

// Reads a JSON file, prints a summary and writes the data back out as JSON.
var path = args.Length > 0 ? args[0] : Path.Combine(AppContext.BaseDirectory, "catalog.json");
if (!File.Exists(path))
{
    Console.Error.WriteLine($"File not found: {path}");
    return 1;
}

Catalog catalog;
try
{
    await using var stream = File.OpenRead(path);
    catalog = await JsonSerializer.DeserializeAsync(stream, CatalogJsonContext.Default.Catalog)
        ?? throw new JsonException("The document is empty.");
}
catch (JsonException e)
{
    Console.Error.WriteLine($"Invalid JSON: {e.Message}");
    return 2;
}

Console.WriteLine($"Catalog: {catalog.Name} (updated {catalog.Updated:yyyy-MM-dd})");
foreach (var product in catalog.Products)
{
    Console.WriteLine($"  #{product.Id} {product.Name,-20} {product.Price,8:F2} {(product.InStock ? "in stock" : "sold out"),-8} [{string.Join(", ", product.Tags)}]");
}

var statistics = CatalogStatistics.From(catalog);
Console.WriteLine($"Products: {statistics.Count}, total {statistics.Total:F2}, average {statistics.Average:F2}");
Console.WriteLine($"Most expensive: {statistics.MostExpensive.Name}");
Console.WriteLine($"Tags: {string.Join(", ", statistics.Tags.Select(static t => $"{t.Tag}={t.Count}"))}");

// The same file through the document model, which needs no type information at all.
using (var document = JsonDocument.Parse(await File.ReadAllBytesAsync(path)))
{
    var names = document.RootElement.GetProperty("products").EnumerateArray().Select(static p => p.GetProperty("name").GetString());
    Console.WriteLine($"Names: {string.Join(" | ", names)}");
}

Console.WriteLine(JsonSerializer.Serialize(catalog with { Products = [.. catalog.Products.Where(static p => p.InStock)] }, CatalogJsonContext.Default.Catalog));
return 0;
