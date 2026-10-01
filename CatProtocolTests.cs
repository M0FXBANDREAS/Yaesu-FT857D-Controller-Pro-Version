using FT857DControl.Models;
using FT857DControl.Services;
using Xunit;

namespace FT857DControl.Tests;

public class CatProtocolTests
{
    [Fact]
    public void Encodes_145_500_MHz_As_Bcd()
    {
        Assert.Equal(new byte[] { 0x14, 0x55, 0x00, 0x00, 0x01 }, CatProtocol.SetFrequency(145_500_000));
    }

    [Fact]
    public void Parses_Frequency_And_Mode()
    {
        var state = CatProtocol.ParseFrequencyAndMode(new byte[] { 0x14, 0x07, 0x40, 0x00, 0x01 });
        Assert.Equal(140_740_000, state.FrequencyHz);
        Assert.Equal(RadioMode.USB, state.Mode);
    }

    [Fact]
    public void Ptt_Uses_Yaesu_Opcodes()
    {
        Assert.Equal(0x08, CatProtocol.SetPtt(true)[4]);
        Assert.Equal(0x88, CatProtocol.SetPtt(false)[4]);
    }

    [Fact]
    public void Ft857d_Repeater_And_Status_Opcodes_Match_Manual()
    {
        Assert.Equal(0x81, CatProtocol.ToggleVfo()[4]);
        Assert.Equal(0x02, CatProtocol.SetSplit(true)[4]);
        Assert.Equal(0x82, CatProtocol.SetSplit(false)[4]);
        Assert.Equal(0xE7, CatProtocol.ReadRxStatus()[4]);
        Assert.Equal(0xF7, CatProtocol.ReadTxStatus()[4]);
        Assert.Equal(0x03, CatProtocol.ReadFrequencyAndMode()[4]);
    }

    [Fact]
    public void Ft857d_Ctcss_Sends_Tx_And_Rx_Tone()
    {
        Assert.Equal(new byte[] { 0x08, 0x85, 0x08, 0x85, 0x0B }, CatProtocol.SetCtcssTone(88.5m));
    }
}
