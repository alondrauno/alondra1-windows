using System.Numerics;
using System.Security.Cryptography;

namespace Alondra1.Core.Cloud.Discovery;

/// <summary>
/// Ed25519 signature verification (RFC 8032), verify only. .NET has no Ed25519 API, and the discovery manifest is the
/// only signature the app checks, so this small, allocation-heavy but straightforward implementation is enough: it
/// runs a few times a day on a 1 KB file. Same acceptance rule as Go's crypto/ed25519 (cofactorless: [S]B - [k]A must
/// encode to R; non-canonical S is rejected), so every client agrees on the shared fixture cases.
/// </summary>
public static class Ed25519
{
    private static readonly BigInteger P = BigInteger.Pow(2, 255) - 19;
    private static readonly BigInteger L = BigInteger.Pow(2, 252) + BigInteger.Parse("27742317777372353535851937790883648493");
    private static readonly BigInteger D = Mod(-121665 * Inverse(121666));
    private static readonly BigInteger SqrtM1 = BigInteger.ModPow(2, (P - 1) / 4, P);
    private static readonly Point B = Decode(Convert.FromHexString("5866666666666666666666666666666666666666666666666666666666666666"))
        ?? throw new InvalidOperationException("base point");

    private readonly record struct Point(BigInteger X, BigInteger Y, BigInteger Z, BigInteger T);

    public static bool Verify(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature)
    {
        if (publicKey.Length != 32 || signature.Length != 64)
            return false;
        var a = Decode(publicKey);
        if (a is null)
            return false;
        var s = new BigInteger(signature[32..], isUnsigned: true, isBigEndian: false);
        if (s >= L)
            return false;
        var hash = SHA512.HashData([.. signature[..32], .. publicKey, .. message]);
        var k = Mod(new BigInteger(hash, isUnsigned: true, isBigEndian: false), L);
        // R' = [S]B + [k](-A) must encode to the R half of the signature.
        var minusA = a.Value with { X = Mod(-a.Value.X), T = Mod(-a.Value.T) };
        var r = Add(Multiply(B, s), Multiply(minusA, k));
        return Encode(r).AsSpan().SequenceEqual(signature[..32]);
    }

    private static BigInteger Mod(BigInteger value, BigInteger? modulus = null)
    {
        var m = modulus ?? P;
        var r = BigInteger.Remainder(value, m);
        return r.Sign < 0 ? r + m : r;
    }

    private static BigInteger Inverse(BigInteger value) => BigInteger.ModPow(Mod(value), P - 2, P);

    private static Point Add(Point p, Point q)
    {
        var a = Mod((p.Y - p.X) * (q.Y - q.X));
        var b = Mod((p.Y + p.X) * (q.Y + q.X));
        var c = Mod(2 * p.T * q.T * D);
        var d = Mod(2 * p.Z * q.Z);
        var e = b - a;
        var f = d - c;
        var g = d + c;
        var h = b + a;
        return new Point(Mod(e * f), Mod(g * h), Mod(f * g), Mod(e * h));
    }

    private static Point Multiply(Point p, BigInteger scalar)
    {
        var result = new Point(0, 1, 1, 0); // neutral element
        var addend = p;
        while (scalar > 0)
        {
            if (!scalar.IsEven)
                result = Add(result, addend);
            addend = Add(addend, addend);
            scalar >>= 1;
        }
        return result;
    }

    private static byte[] Encode(Point p)
    {
        var zInv = Inverse(p.Z);
        var x = Mod(p.X * zInv);
        var y = Mod(p.Y * zInv);
        var bytes = new byte[32];
        y.TryWriteBytes(bytes, out _, isUnsigned: true, isBigEndian: false);
        if (!x.IsEven)
            bytes[31] |= 0x80;
        return bytes;
    }

    private static Point? Decode(ReadOnlySpan<byte> encoded)
    {
        var bytes = encoded.ToArray();
        var sign = (bytes[31] & 0x80) != 0;
        bytes[31] &= 0x7F;
        var y = new BigInteger(bytes, isUnsigned: true, isBigEndian: false);
        if (y >= P)
            return null; // non-canonical
        var u = Mod(y * y - 1);
        var v = Mod(D * y * y + 1);
        // x = u * v^3 * (u * v^7)^((p-5)/8)
        var v3 = Mod(v * v * v);
        var x = Mod(u * v3 * BigInteger.ModPow(Mod(u * v3 * v3 * v), (P - 5) / 8, P));
        var vx2 = Mod(v * x * x);
        if (vx2 != u)
        {
            if (vx2 != Mod(-u))
                return null;
            x = Mod(x * SqrtM1);
        }
        if (x.IsZero && sign)
            return null;
        if (!x.IsEven != sign)
            x = Mod(-x);
        return new Point(x, y, 1, Mod(x * y));
    }
}
