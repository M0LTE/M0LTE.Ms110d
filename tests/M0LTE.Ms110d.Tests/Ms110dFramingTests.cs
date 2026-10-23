using M0LTE.Ms110d;

namespace M0LTE.Ms110d.Tests;

public class Ms110dFramingTests
{
    [Fact]
    public void Eom_Is_Appended_Leftmost_Bit_First()
    {
        // D.5.4.3 (checklist L7): 0x4B65A5B2, "left most bit is sent first" -
        // 0100 1011 0110 0101 1010 0101 1011 0010.
        byte[] bits = Ms110dFraming.BuildTxBits(new byte[8], appendEom: true, inputBits: 48);
        bits.Length.Should().Be(48);
        byte[] expectedEom =
        [
            0, 1, 0, 0, 1, 0, 1, 1, 0, 1, 1, 0, 0, 1, 0, 1,
            1, 0, 1, 0, 0, 1, 0, 1, 1, 0, 1, 1, 0, 0, 1, 0,
        ];
        bits.Skip(8).Take(32).Should().Equal(expectedEom);
        bits.Skip(40).Should().AllBeEquivalentTo(0, "zero fill to the input-data-block boundary");
    }

    [Fact]
    public void Short_Payload_Still_Occupies_One_Whole_Block()
    {
        Ms110dFraming.BuildTxBits([1, 0, 1], appendEom: false, inputBits: 96).Length.Should().Be(96);
        Ms110dFraming.BuildTxBits([], appendEom: true, inputBits: 24).Length.Should().Be(48);
    }

    [Fact]
    public void FindEom_Locates_The_Marker_Across_A_Block_Boundary()
    {
        byte[] bits = Ms110dFraming.BuildTxBits(new byte[100], appendEom: true, inputBits: 96);
        var list = new List<byte>(bits);
        Ms110dFraming.FindEom(list, 0).Should().Be(100);
        Ms110dFraming.FindEom(list, 96 - 31).Should().Be(100, "the scan window spans block joins");
    }
}
