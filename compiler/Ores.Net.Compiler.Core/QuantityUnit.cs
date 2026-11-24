using System.Numerics;

namespace Ores.Net.Compiler;

// File-local, canonical quantity types. A name is resolved by the reader before
// forming a literal; only the normalized dimension and scale affect typing.
internal sealed class QuantityUnit
{
    private const int MaxDimensions = 32;
    private const int MaxExponent = 64;
    private const int MaxScaleBits = 256;

    private readonly SortedDictionary<string, int> dimensions;
    private readonly BigInteger numerator;
    private readonly BigInteger denominator;

    internal static QuantityUnit Scalar { get; } = new(new(StringComparer.Ordinal), 1, 1);
    internal bool IsScalar => dimensions.Count == 0 && numerator.IsOne && denominator.IsOne;

    private QuantityUnit(SortedDictionary<string, int> dimensions,
        BigInteger numerator, BigInteger denominator)
    {
        if (numerator.Sign <= 0 || denominator.Sign <= 0)
            throw new FormatException("E_UNIT_SCALE: a unit scale must be positive");
        var gcd = BigInteger.GreatestCommonDivisor(numerator, denominator);
        this.numerator = numerator / gcd;
        this.denominator = denominator / gcd;
        if (this.numerator.GetBitLength() > MaxScaleBits || this.denominator.GetBitLength() > MaxScaleBits)
            throw new FormatException("E_UNIT_LIMIT: normalized scale exceeds 256 bits");
        this.dimensions = dimensions;
    }

    internal static QuantityUnit Base(string dimension) =>
        new(new(StringComparer.Ordinal) { [dimension] = 1 }, 1, 1);

    internal QuantityUnit Scale(BigInteger factor, bool divide) => divide
        ? new(dimensions, numerator, denominator * factor)
        : new(dimensions, numerator * factor, denominator);

    internal bool HasDimension(QuantityUnit other) => dimensions.SequenceEqual(other.dimensions);
    internal bool SameUnit(QuantityUnit other) =>
        numerator == other.numerator && denominator == other.denominator && HasDimension(other);

    internal void RequireCompatible(QuantityUnit other)
    {
        if (!HasDimension(other))
            throw new FormatException("E_UNIT_DIMENSION: incompatible quantity dimensions");
        if (numerator != other.numerator || denominator != other.denominator)
            throw new FormatException("E_UNIT_SCALE: quantities require an explicit scale conversion");
    }

    internal QuantityUnit Combine(QuantityUnit other, bool divide)
    {
        var result = new SortedDictionary<string, int>(dimensions, StringComparer.Ordinal);
        foreach (var (dimension, power) in other.dimensions)
        {
            var exponent = result.GetValueOrDefault(dimension) + (divide ? -power : power);
            if (Math.Abs(exponent) > MaxExponent)
                throw new FormatException("E_UNIT_LIMIT: dimension exponent exceeds 64");
            if (exponent == 0) result.Remove(dimension);
            else result[dimension] = exponent;
        }
        if (result.Count > MaxDimensions)
            throw new FormatException("E_UNIT_LIMIT: quantity has more than 32 base dimensions");
        return divide
            ? new(result, numerator * other.denominator, denominator * other.numerator)
            : new(result, numerator * other.numerator, denominator * other.denominator);
    }

    internal long ConvertExact(long value, QuantityUnit target)
    {
        if (!HasDimension(target))
            throw new FormatException("E_UNIT_DIMENSION: conversion requires the same dimension");
        var converted = BigInteger.DivRem(value * numerator * target.denominator,
            denominator * target.numerator, out var remainder);
        if (!remainder.IsZero)
            throw new FormatException("E_UNIT_INEXACT: conversion requires an integral result");
        if (converted < long.MinValue || converted > long.MaxValue)
            throw new FormatException("E_UNIT_RANGE: conversion result is outside signed i64");
        return (long)converted;
    }
}

internal sealed record QuantityLiteral(long Value, QuantityUnit Unit) : Expression;

// A source slash is admitted in the quantity profile. Plain scalar division
// retains its existing source rules, including when dimensions cancel earlier.
internal sealed record QuantityDivision(Expression Left, Expression Right) : Expression;
