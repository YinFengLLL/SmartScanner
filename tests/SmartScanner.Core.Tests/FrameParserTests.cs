using System.Text;
using SmartScanner.Core.Serial;

namespace SmartScanner.Core.Tests;

public class FrameParserTests
{
    [Fact]
    public void SplitFrames_完整帧CRLF结尾_返回单帧并去掉CR()
    {
        var carry = new StringBuilder();
        var frames = FrameParser.SplitFrames("ABC123\r\n", carry);
        Assert.Equal(["ABC123"], frames);
        Assert.Equal("", carry.ToString());
    }

    [Fact]
    public void SplitFrames_分包到达_累积到行尾才出帧()
    {
        var carry = new StringBuilder();
        Assert.Empty(FrameParser.SplitFrames("AB", carry));
        Assert.Empty(FrameParser.SplitFrames("C12", carry));

        var frames = FrameParser.SplitFrames("3\n", carry);
        Assert.Equal(["ABC123"], frames);
        Assert.Equal("", carry.ToString());
    }

    [Fact]
    public void SplitFrames_仅LF结尾_同样切帧()
    {
        var carry = new StringBuilder();
        var frames = FrameParser.SplitFrames("CODE42\n", carry);
        Assert.Equal(["CODE42"], frames);
    }

    [Fact]
    public void SplitFrames_仅CR结尾_同样切帧() // 斯普锐等扫码枪可配置 CR 结束符
    {
        var carry = new StringBuilder();
        var frames = FrameParser.SplitFrames("CODE99\r", carry);
        Assert.Equal(["CODE99"], frames);
    }

    [Fact]
    public void SplitFrames_三种结束符混合_全部正确切帧()
    {
        var carry = new StringBuilder();
        var frames = FrameParser.SplitFrames("A\r\nB\nC\r", carry);
        Assert.Equal(["A", "B", "C"], frames);
    }

    [Fact]
    public void SplitFrames_一包多帧_全部切出()
    {
        var carry = new StringBuilder();
        var frames = FrameParser.SplitFrames("A1\r\nB2\r\n", carry);
        Assert.Equal(["A1", "B2"], frames);
    }

    [Fact]
    public void SplitFrames_空行忽略()
    {
        var carry = new StringBuilder();
        var frames = FrameParser.SplitFrames("\r\n\r\n", carry);
        Assert.Empty(frames);
    }
}
