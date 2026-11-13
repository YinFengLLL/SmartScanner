using System.Text;
using SmartScanner.Core.Models;
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

    [Fact]
    public void CleanFrame_去掉STX_ETX包裹()
    {
        Assert.Equal("ABC123", FrameParser.CleanFrame("\u0002ABC123\u0003"));
    }

    [Fact]
    public void CleanFrame_去掉两端不可见字符与空白()
    {
        Assert.Equal("6901234567890", FrameParser.CleanFrame(" \r\n6901234567890\u0000\r\n"));
    }

    [Fact]
    public void CleanFrame_纯噪声返回空串()
    {
        Assert.Equal("", FrameParser.CleanFrame("\u0002\u0003"));
        Assert.Equal("", FrameParser.CleanFrame(""));
        Assert.Equal("", FrameParser.CleanFrame(null));
    }

    [Fact]
    public void CleanFrame_关闭去前后缀时原样返回()
    {
        Assert.Equal("\u0002ABC123\u0003", FrameParser.CleanFrame("\u0002ABC123\u0003", trimAffixes: false));
    }

    [Fact]
    public void SplitFrames_指定CRLF_只有CRLF才切帧()
    {
        Assert.Empty(FrameParser.SplitFrames("A1\r", new StringBuilder(), ScannerFrameMode.CrLf));
        Assert.Empty(FrameParser.SplitFrames("A1\n", new StringBuilder(), ScannerFrameMode.CrLf));
        Assert.Equal(["A1"], FrameParser.SplitFrames("A1\r\n", new StringBuilder(), ScannerFrameMode.CrLf));
    }

    [Fact]
    public void SplitFrames_指定CR_只有CR才切帧()
    {
        Assert.Empty(FrameParser.SplitFrames("A1\n", new StringBuilder(), ScannerFrameMode.Cr));
        Assert.Equal(["A1"], FrameParser.SplitFrames("A1\r", new StringBuilder(), ScannerFrameMode.Cr));
    }

    [Fact]
    public void SplitFrames_指定LF_只有LF才切帧()
    {
        Assert.Empty(FrameParser.SplitFrames("A1\r", new StringBuilder(), ScannerFrameMode.Lf));
        Assert.Equal(["A1"], FrameParser.SplitFrames("A1\n", new StringBuilder(), ScannerFrameMode.Lf));
    }

    [Fact]
    public void SplitFrames_无前后缀模式_不按结束符切帧()
    {
        var carry = new StringBuilder();
        Assert.Empty(FrameParser.SplitFrames("6901234567890", carry, ScannerFrameMode.None));
        Assert.Equal("6901234567890", carry.ToString()); // 交由传输层静默分帧
    }
}
