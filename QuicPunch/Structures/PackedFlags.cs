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
        get => (QuicPunch.NetworkType)(_data & 0b111);
        set
        {
            _data = (byte)((_data & ~0b111) | ((byte)value & 0b111));
        }
    }
    public byte RawValue => _data;
}
