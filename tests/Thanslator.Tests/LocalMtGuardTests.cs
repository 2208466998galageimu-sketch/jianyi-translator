using Thanslator.Translation;

namespace Thanslator.Tests;

public sealed class LocalMtGuardTests
{
    [Fact]
    public void ValidateOutput_DoesNotLetSourceMarkerHideAnotherArtifact()
    {
        Assert.Null(LocalMtGuard.ValidateOutput("Read T1 now.", "现在读取 T1。"));
        Assert.NotNull(LocalMtGuard.ValidateOutput("Read T1 now.", "现在读取 T2。"));
    }

    [Theory]
    [InlineData("结果 >>cmn_Hans<< >>cmn_Hans<<")]
    [InlineData("访问 ⟦0 并读取文档")]
    [InlineData("我使用 T0 和 T1")]
    [InlineData("这是错误。这是错误。这是错误。这是错误。")]
    public void ValidateOutput_RejectsModelArtifacts(string output)
    {
        Assert.NotNull(LocalMtGuard.ValidateOutput("Read the documentation.", output));
    }
    [Theory]
    [InlineData("This is a sentence.")]
    [InlineData("Hello, world!")]
    [InlineData("Use GitHub and Docker.")]
    public void ValidateInput_AllowsEnglish(string text)
    {
        Assert.True(LocalMtGuard.ValidateInput(text).Allowed);
    }

    [Theory]
    [InlineData("这是中文句子。")]
    [InlineData("こんにちは世界")]
    [InlineData("안녕하세요 세계")]
    public void ValidateInput_BlocksCjkText(string text)
    {
        var result = LocalMtGuard.ValidateInput(text);

        Assert.False(result.Allowed);
        Assert.Contains("英→中", result.Message);
    }

    [Fact]
    public void ValidateInput_BlocksSymbolsOnly()
    {
        Assert.False(LocalMtGuard.ValidateInput("... !!! ???").Allowed);
    }

    [Fact]
    public void ValidateInput_BlocksEmpty()
    {
        Assert.False(LocalMtGuard.ValidateInput("   ").Allowed);
    }

    [Fact]
    public void ValidateOutput_AcceptsNormalChinese()
    {
        Assert.Null(LocalMtGuard.ValidateOutput("This is a sentence.", "这是一个句子。"));
    }

    [Fact]
    public void ValidateOutput_RejectsEmptyAndGarbage()
    {
        Assert.NotNull(LocalMtGuard.ValidateOutput("Hello world.", ""));
        Assert.NotNull(LocalMtGuard.ValidateOutput("Hello world.", "�"));
        Assert.NotNull(LocalMtGuard.ValidateOutput("Hello world.", "!!!"));
    }

    [Fact]
    public void ValidateOutput_RejectsUnchangedSentence()
    {
        Assert.NotNull(LocalMtGuard.ValidateOutput("Hello world.", "Hello world."));
    }
}
