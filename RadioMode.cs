namespace FT857DControl.Models;

public enum RadioMode : byte
{
    LSB = 0x00, USB = 0x01, CW = 0x02, CWR = 0x03,
    AM = 0x04, WFM = 0x06, FM = 0x08, DIG = 0x0A, PKT = 0x0C
}

public readonly record struct RadioState(long FrequencyHz, RadioMode Mode);
