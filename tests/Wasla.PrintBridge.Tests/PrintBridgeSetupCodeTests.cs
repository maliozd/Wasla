using Wasla.Infrastructure.Security;

namespace Wasla.PrintBridge.Tests;

public sealed class PrintBridgeSetupCodeTests
{
    [Fact]
    public void Generate_ProducesUrlSafeValueOfExpectedStrength()
    {
        var code = PrintBridgeSetupCode.Generate();

        // 32 random bytes (256 bits) Base64Url-encoded without padding → 43 chars.
        Assert.Equal(43, code.Length);
        Assert.DoesNotContain('+', code);
        Assert.DoesNotContain('/', code);
        Assert.DoesNotContain('=', code);
    }

    [Fact]
    public void Generate_ProducesUniqueValues()
    {
        var values = Enumerable.Range(0, 200).Select(_ => PrintBridgeSetupCode.Generate()).ToHashSet();

        Assert.Equal(200, values.Count);
    }

    [Fact]
    public void Hash_IsDeterministicAndNotThePlainValue()
    {
        var code = PrintBridgeSetupCode.Generate();

        var hash1 = PrintBridgeSetupCode.Hash(code);
        var hash2 = PrintBridgeSetupCode.Hash(code);

        Assert.Equal(hash1, hash2);
        Assert.NotEqual(code, hash1);
    }

    [Fact]
    public void Verify_MatchesCorrectValueAndRejectsOthers()
    {
        var code = PrintBridgeSetupCode.Generate();
        var hash = PrintBridgeSetupCode.Hash(code);

        Assert.True(PrintBridgeSetupCode.Verify(code, hash));
        Assert.False(PrintBridgeSetupCode.Verify(PrintBridgeSetupCode.Generate(), hash));
        Assert.False(PrintBridgeSetupCode.Verify(null, hash));
        Assert.False(PrintBridgeSetupCode.Verify(code, null));
    }

    [Fact]
    public void Hash_ThrowsForEmptyValue()
    {
        Assert.Throws<ArgumentException>(() => PrintBridgeSetupCode.Hash(""));
    }
}
