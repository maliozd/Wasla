namespace OrderHub.Web.Ui;

public static class OrderProductImageHelper
{
    private static readonly string[] DemoImages =
    [
        "/images/demo-food/chicken-rice-01.svg",
        "/images/demo-food/chicken-rice-02.svg",
        "/images/demo-food/chicken-rice-03.svg",
        "/images/demo-food/chicken-rice-04.svg",
        "/images/demo-food/chicken-rice-05.svg"
    ];

    public static string ResolveDisplayImageUrl(string? productImageUrl, string seed)
    {
        if (!string.IsNullOrWhiteSpace(productImageUrl))
            return productImageUrl.Trim();

        var normalizedSeed = string.IsNullOrWhiteSpace(seed) ? "order" : seed.Trim();
        var index = Math.Abs(StableHash(normalizedSeed)) % DemoImages.Length;
        return DemoImages[index];
    }

    public static string BuildImageSeed(Guid orderId, string externalOrderCode, string? firstProductName)
    {
        if (!string.IsNullOrWhiteSpace(firstProductName))
            return firstProductName.Trim();

        if (!string.IsNullOrWhiteSpace(externalOrderCode))
            return externalOrderCode.Trim();

        return orderId.ToString("N");
    }

    private static int StableHash(string value)
    {
        unchecked
        {
            var hash = 17;
            foreach (var c in value)
                hash = (hash * 31) + c;

            return hash;
        }
    }
}
