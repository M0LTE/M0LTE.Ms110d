// Complex32: a single-precision complex number for M0LTE.Ms110d.
//
// This type is original to this package. It was written from scratch for M0LTE.Ms110d from the
// members the modem actually uses and textbook complex arithmetic, and it is not derived from
// any other library's complex type. It is licensed with the rest of the package
// (AGPL-3.0-or-later).

using System.Runtime.CompilerServices;

namespace M0LTE.Ms110d;

/// <summary>A single-precision complex number (real and imaginary parts as <see cref="float"/>).</summary>
public readonly struct Complex32 : IEquatable<Complex32>
{
    /// <summary>Real part.</summary>
    public readonly float Re;

    /// <summary>Imaginary part.</summary>
    public readonly float Im;

    /// <summary>Creates <c>re + j im</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Complex32(float re, float im)
    {
        Re = re;
        Im = im;
    }

    /// <summary>Zero.</summary>
    public static Complex32 Zero => default;

    /// <summary>The unit phasor <c>cos(phase) + j sin(phase)</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Complex32 Cmplx(float phase) => new(MathF.Cos(phase), MathF.Sin(phase));

    /// <summary>The conjugate unit phasor <c>cos(phase) - j sin(phase)</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Complex32 CmplxConj(float phase) => new(MathF.Cos(phase), -MathF.Sin(phase));

    /// <summary>The complex conjugate.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Complex32 Conj() => new(Re, -Im);

    /// <summary>The squared magnitude <c>re^2 + im^2</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float Cnorm() => (Re * Re) + (Im * Im);

    /// <summary>The magnitude <c>sqrt(re^2 + im^2)</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float Abs() => MathF.Sqrt((Re * Re) + (Im * Im));

    /// <summary>The argument (phase angle) in radians, in (-pi, pi].</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float Arg() => MathF.Atan2(Im, Re);

    /// <summary>Sum.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Complex32 operator +(Complex32 a, Complex32 b) => new(a.Re + b.Re, a.Im + b.Im);

    /// <summary>Difference.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Complex32 operator -(Complex32 a, Complex32 b) => new(a.Re - b.Re, a.Im - b.Im);

    /// <summary>Negation.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Complex32 operator -(Complex32 a) => new(-a.Re, -a.Im);

    /// <summary>Product.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Complex32 operator *(Complex32 a, Complex32 b) =>
        new((a.Re * b.Re) - (a.Im * b.Im), (a.Re * b.Im) + (a.Im * b.Re));

    /// <summary>Product.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Complex32 operator *(Complex32 a, float s) => new(a.Re * s, a.Im * s);

    /// <summary>Product.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Complex32 operator *(float s, Complex32 a) => new(s * a.Re, s * a.Im);

    /// <inheritdoc />
    public bool Equals(Complex32 other) => Re == other.Re && Im == other.Im;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is Complex32 c && Equals(c);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Re, Im);

    /// <summary>Exact equality.</summary>
    public static bool operator ==(Complex32 a, Complex32 b) => a.Equals(b);

    /// <summary>Exact inequality.</summary>
    public static bool operator !=(Complex32 a, Complex32 b) => !a.Equals(b);

    /// <inheritdoc />
    public override string ToString() => $"({Re}, {Im})";
}
