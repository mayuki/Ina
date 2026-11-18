using System.Text.Json.Serialization;

namespace JsonReader.Models;

public sealed record Catalog(string Name, DateOnly Updated, List<Product> Products);

public sealed record Product(int Id, string Name, decimal Price, string[] Tags, bool InStock = true);

/// <summary>
/// Source generated serialization metadata. Reflection-based serialization is not available in ahead-of-time
/// compiled applications, so the types are listed here.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Catalog))]
public sealed partial class CatalogJsonContext : JsonSerializerContext;
