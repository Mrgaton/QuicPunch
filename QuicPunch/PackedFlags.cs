namespace QuicPunch;
public class PackedFlags
{
    public PackedFlags() {  }
    public PackedFlags(byte value)
    {
        _data = value;
    }

    private byte _data;

    public QuicPunch.NetworkType NetworkType
    {
        get => (QuicPunch.NetworkType)(_data & 0b11);
        set
        {
            _data = (byte)((_data & ~0b11) | (byte)value);
        }
    }
    public bool UnusedFlag2
    {
        get => GetBit(2);
        set => SetBit(2, value);
    }

    public bool UnusedFlag3
    {
        get => GetBit(3);
        set => SetBit(3, value);
    }

    public bool UnusedFlag4
    {
        get => GetBit(4);
        set => SetBit(4, value);
    }

    public bool UnusedFlag5
    {
        get => GetBit(5);
        set => SetBit(5, value);
    }

    public bool UnusedFlag6
    {
        get => GetBit(6);
        set => SetBit(6, value);
    }

    public bool UnusedFlag7
    {
        get => GetBit(7);
        set => SetBit(7, value);
    }
    
    /*public int SmallNumber
    {
        get => _data & 0b11;
        set
        {
            if (value < 0 || value > 3)
                throw new ArgumentOutOfRangeException(nameof(value), "Must be between 0 and 3.");

            _data = (byte)((_data & ~0b11) | value);
        }
    }*/

    public byte RawValue => _data;

    private bool GetBit(int bit)
    {
        return (_data & (1 << bit)) != 0;
    }

    private void SetBit(int bit, bool value)
    {
        if (value)
            _data = (byte)(_data | (1 << bit));
        else
            _data = (byte)(_data & ~(1 << bit));
    }
}