using M0LTE.Ms110d;

namespace M0LTE.Ms110d.Tests;

public class Trinomial159ScramblerTests
{
    // D.5.1.4 printed golden vector (text-layer verbatim,
    // docs/dev/ms110d/tables/text-layer-extracts.md): "the first 32 symbols of the scramble
    // sequence are" -
    private static readonly int[] PrintedFirst32 =
    [
        5, 6, 2, 1, 7, 3, 1, 1, 6, 0, 5, 4, 0, 7, 7, 0,
        5, 3, 1, 3, 3, 2, 2, 5, 5, 4, 7, 3, 5, 4, 3, 0,
    ];

    [Fact]
    public void Regenerates_The_Printed_First_32_Scramble_Symbols()
    {
        var scrambler = new Trinomial159Scrambler();
        var symbols = new int[32];
        for (int i = 0; i < 32; i++)
        {
            symbols[i] = scrambler.Next();
        }

        symbols.Should().Equal(PrintedFirst32);
    }

    [Fact]
    public void Reset_Restarts_The_Sequence()
    {
        var scrambler = new Trinomial159Scrambler();
        for (int i = 0; i < 100; i++)
        {
            scrambler.Next();
        }

        scrambler.Reset();
        scrambler.Next().Should().Be(PrintedFirst32[0]);
    }

    [Fact]
    public void Wraps_At_The_2048_Symbol_Boundary()
    {
        // D.5.1.4: "For the Walsh Orthogonal Modes the sequences are continuously wrapped
        // around the 2048 symbol boundary."
        var scrambler = new Trinomial159Scrambler();
        var first32 = new int[32];
        for (int i = 0; i < 32; i++)
        {
            first32[i] = scrambler.Next();
        }

        for (int i = 32; i < 2048; i++)
        {
            scrambler.Next();
        }

        var wrapped = new int[32];
        for (int i = 0; i < 32; i++)
        {
            wrapped[i] = scrambler.Next();
        }

        wrapped.Should().Equal(first32);
    }
}
