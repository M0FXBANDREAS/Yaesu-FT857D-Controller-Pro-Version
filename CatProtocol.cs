using FT857DControl.Models;

namespace FT857DControl.Services;

public static class CatProtocol
{
    public static byte[] SetFrequency(long frequencyHz)
    {
        if (frequencyHz is < 100_000 or > 470_000_000)
            throw new ArgumentOutOfRangeException(nameof(frequencyHz), "Frequency must be between 0.1 and 470 MHz.");

        var tenHz = frequencyHz / 10;
        var digits = tenHz.ToString("D8");
        return
        [
            Pack(digits[0], digits[1]), Pack(digits[2], digits[3]),
            Pack(digits[4], digits[5]), Pack(digits[6], digits[7]), 0x01
        ];
    }

    public static byte[] SetMode(RadioMode mode) => [(byte)mode, 0, 0, 0, 0x07];
    public static byte[] ReadFrequencyAndMode() => [0, 0, 0, 0, 0x03];
    public static byte[] ToggleVfo() => [0, 0, 0, 0, 0x81];
    public static byte[] SetSplit(bool on) => [0, 0, 0, 0, on ? (byte)0x02 : (byte)0x82];
    public static byte[] SetPtt(bool transmit) => [0, 0, 0, 0, transmit ? (byte)0x08 : (byte)0x88];
    public static byte[] ReadRxStatus() => [0, 0, 0, 0, 0xE7];
    public static byte[] ReadTxStatus() => [0, 0, 0, 0, 0xF7];
    public static byte[] SetRepeaterShift(int direction) => [direction < 0 ? (byte)0x09 : direction > 0 ? (byte)0x49 : (byte)0x89, 0, 0, 0, 0x09];
    public static byte[] SetRepeaterOffset(long offsetHz)
    {
        offsetHz = Math.Clamp(Math.Abs(offsetHz), 0, 99_999_990);
        var digits = (offsetHz / 10).ToString("D8");
        return [Pack(digits[0], digits[1]), Pack(digits[2], digits[3]), Pack(digits[4], digits[5]), Pack(digits[6], digits[7]), 0xF9];
    }
    public static byte[] SetCtcssMode(bool enabled) => [enabled ? (byte)0x4A : (byte)0x8A, 0, 0, 0, 0x0A];
    public static byte[] SetCtcssTone(decimal toneHz)
    {
        var tenths = decimal.ToInt32(decimal.Round(toneHz * 10m));
        if (tenths < 670 || tenths > 2541) throw new ArgumentOutOfRangeException(nameof(toneHz));
        var digits = tenths.ToString("D4");
        return [Pack(digits[0], digits[1]), Pack(digits[2], digits[3]), Pack(digits[0], digits[1]), Pack(digits[2], digits[3]), 0x0B];
    }
    public static byte[] SetClarifier(bool on) => [0, 0, 0, 0, on ? (byte)0x05 : (byte)0x85];
    public static byte[] SetClarifierOffset(int offsetHz)
    {
        offsetHz = Math.Clamp(offsetHz, -9990, 9990);
        var magnitude10Hz = Math.Abs(offsetHz) / 10;
        var digits = magnitude10Hz.ToString("D4");
        return [offsetHz < 0 ? (byte)0x01 : (byte)0x00, 0x00,
            Pack(digits[0], digits[1]), Pack(digits[2], digits[3]), 0xF5];
    }

    public static RadioState ParseFrequencyAndMode(ReadOnlySpan<byte> response)
    {
        if (response.Length != 5) throw new ArgumentException("CAT frequency response must be five bytes.");
        long tenHz = 0;
        for (var i = 0; i < 4; i++)
        {
            var high = (response[i] >> 4) & 0x0F;
            var low = response[i] & 0x0F;
            if (high > 9 || low > 9) throw new FormatException("Radio returned invalid BCD frequency data.");
            tenHz = tenHz * 100 + high * 10 + low;
        }
        return new RadioState(tenHz * 10, (RadioMode)response[4]);
    }

    private static byte Pack(char high, char low) =>
        (byte)(((high - '0') << 4) | (low - '0'));
}
