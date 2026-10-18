using System.Text;
using Wasla.Application.Branding;

namespace Wasla.UnitTests.Branding;

/// <summary>WAS-127: which uploads are accepted as a restaurant logo, recognized from the bytes alone.</summary>
public sealed class RestaurantLogoImageTests
{
    [Fact]
    public void Png_IsAccepted_WithItsSize()
    {
        var result = RestaurantLogoImage.Inspect(TestImages.Png(64, 32));

        Assert.Equal(new RestaurantLogoInspection(RestaurantLogoError.None, RestaurantLogoImage.Png, 64, 32), result);
    }

    [Fact]
    public void Jpeg_IsAccepted_WithItsSize()
    {
        var result = RestaurantLogoImage.Inspect(TestImages.JpegHeader(300, 200));

        Assert.Equal(new RestaurantLogoInspection(RestaurantLogoError.None, RestaurantLogoImage.Jpeg, 300, 200), result);
    }

    [Theory]
    [InlineData("VP8 ")]
    [InlineData("VP8L")]
    [InlineData("VP8X")]
    public void WebP_IsAccepted_WithItsSize(string chunk)
    {
        var result = RestaurantLogoImage.Inspect(TestImages.WebP(chunk, 512, 128));

        Assert.Equal(new RestaurantLogoInspection(RestaurantLogoError.None, RestaurantLogoImage.WebP, 512, 128), result);
    }

    [Fact]
    public void Empty_IsRefused() =>
        Assert.Equal(RestaurantLogoError.Empty, RestaurantLogoImage.Inspect([]).Error);

    [Fact]
    public void MoreThanTheLimit_IsRefused_BeforeItIsRead()
    {
        var png = TestImages.Png(8, 8);
        var oversized = new byte[RestaurantLogoImage.MaxBytes + 1];
        png.CopyTo(oversized, 0);

        Assert.Equal(RestaurantLogoError.TooLarge, RestaurantLogoImage.Inspect(oversized).Error);
        Assert.True(RestaurantLogoImage.Inspect(png).IsValid);
    }

    [Theory]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>")]
    [InlineData("<?xml version=\"1.0\"?><svg xmlns=\"http://www.w3.org/2000/svg\"/>")]
    [InlineData("<!DOCTYPE html><html><body><script>alert(1)</script></body></html>")]
    [InlineData("GIF89a\u0001\u0000\u0001\u0000\u0000\u0000\u0000;")]
    [InlineData("MZ\u0090\u0000 not an image")]
    [InlineData("BM6\u0000\u0000\u0000\u0000\u0000\u0000\u00006\u0000\u0000\u0000")]
    public void OtherFormats_AreRefused_WhateverTheFileIsCalled(string content) =>
        Assert.Equal(RestaurantLogoError.UnsupportedFormat, RestaurantLogoImage.Inspect(Encoding.Latin1.GetBytes(content)).Error);

    [Fact]
    public void PngWithoutImageData_IsRefused() =>
        Assert.Equal(RestaurantLogoError.InvalidImage, RestaurantLogoImage.Inspect(TestImages.PngHeaderOnly(64, 64)).Error);

    [Fact]
    public void TruncatedPng_IsRefused()
    {
        var png = TestImages.Png(16, 16);

        Assert.Equal(RestaurantLogoError.InvalidImage, RestaurantLogoImage.Inspect(png[..^12]).Error);
    }

    [Fact]
    public void PngSignatureFollowedByHtml_IsRefused()
    {
        var content = TestImages.Png(1, 1)[..8].Concat(Encoding.ASCII.GetBytes("<html><script>alert(1)</script></html>")).ToArray();

        Assert.Equal(RestaurantLogoError.InvalidImage, RestaurantLogoImage.Inspect(content).Error);
    }

    [Fact]
    public void PngWithZeroWidth_IsRefused()
    {
        var png = TestImages.Png(4, 4);
        png[16] = png[17] = png[18] = png[19] = 0;

        Assert.Equal(RestaurantLogoError.InvalidImage, RestaurantLogoImage.Inspect(png).Error);
    }

    [Fact]
    public void JpegWithoutAFrameHeader_IsRefused()
    {
        byte[] scanFirst = [0xFF, 0xD8, 0xFF, 0xDA, 0x00, 0x08, 0x01, 0x01, 0x00, 0x00, 0x3F, 0x00, 0xFF, 0xD9];

        Assert.Equal(RestaurantLogoError.InvalidImage, RestaurantLogoImage.Inspect(scanFirst).Error);
    }

    [Fact]
    public void WebPWhoseContainerIsLongerThanTheFile_IsRefused()
    {
        var webp = TestImages.WebP("VP8 ", 32, 32);

        Assert.Equal(RestaurantLogoError.InvalidImage, RestaurantLogoImage.Inspect(webp[..^4]).Error);
    }

    [Theory]
    [InlineData(RestaurantLogoImage.MaxDimension + 1, 10)]
    [InlineData(10, RestaurantLogoImage.MaxDimension + 1)]
    public void ImagesLargerThanTheMaximumSize_AreRefused(int width, int height)
    {
        Assert.Equal(RestaurantLogoError.DimensionsTooLarge, RestaurantLogoImage.Inspect(TestImages.JpegHeader(width, height)).Error);
        Assert.Equal(RestaurantLogoError.DimensionsTooLarge, RestaurantLogoImage.Inspect(TestImages.WebP("VP8X", width, height)).Error);
        Assert.True(RestaurantLogoImage.Inspect(TestImages.JpegHeader(RestaurantLogoImage.MaxDimension, RestaurantLogoImage.MaxDimension)).IsValid);
    }
}
