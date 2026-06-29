using System.ComponentModel.DataAnnotations;

namespace eShop.Catalog.API.Model;

public record UpdateCatalogItemRequest(
    [property: Required] string Name,
    string? Description,
    [property: Required] decimal Price,
    [property: Required] string? PictureFileName,
    [property: Required] int CatalogTypeId,
    [property: Required] int CatalogBrandId,
    [property: Required] int AvailableStock,
    [property: Required] int RestockThreshold,
    [property: Required] int MaxStockThreshold);
