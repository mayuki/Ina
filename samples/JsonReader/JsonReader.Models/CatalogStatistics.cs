namespace JsonReader.Models;

/// <summary>Aggregates computed over a catalog; lives in the library to exercise calls across assemblies.</summary>
public sealed record CatalogStatistics(int Count, decimal Total, decimal Average, Product MostExpensive, IReadOnlyList<(string Tag, int Count)> Tags)
{
    public static CatalogStatistics From(Catalog catalog)
    {
        var products = catalog.Products;
        var tags = products
            .SelectMany(static p => p.Tags)
            .GroupBy(static tag => tag)
            .Select(static g => (Tag: g.Key, Count: g.Count()))
            .OrderByDescending(static t => t.Count)
            .ThenBy(static t => t.Tag, StringComparer.Ordinal)
            .ToList();

        return new CatalogStatistics(
            products.Count,
            products.Sum(static p => p.Price),
            products.Count == 0 ? 0 : decimal.Round(products.Average(static p => p.Price), 2),
            products.MaxBy(static p => p.Price)!,
            tags);
    }
}
