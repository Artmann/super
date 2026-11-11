namespace Super.Materials;

/// <summary>
/// Sine, cosine, power, exponent, log and atan2 that give the same bits on every machine.
/// The framework's versions can differ by platform, so graphs never call them. Each works in
/// <c>double</c> with range reduction and polynomials, using only exact operations, then rounds to <c>float</c>.
/// </summary>
public static class DMath
{
    const double PiOver2Hi = 1.57079632673412561417e+00;
    const double PiOver2Mid = 6.07710050630396597660e-11;
    const double PiOver2Lo = 2.02226624879595063154e-21;
    const double Ln2Hi = 6.93147180369123816490e-01;
    const double Ln2Lo = 1.90821492927058770002e-10;
    const double Ln2 = Ln2Hi + Ln2Lo;

    public static float Sin(float x) => (float)SinD(x);

    public static float Cos(float x) => (float)CosD(x);

    public static float Exp(float x) => (float)ExpD(x);

    public static float Log(float x) => (float)LogD(x);

    public static float Pow(float x, float y) => (float)PowD(x, y);

    public static float Atan2(float y, float x) => (float)Atan2D(y, x);

    static double SinD(double x)
    {
        if (!double.IsFinite(x))
        {
            return double.NaN;
        }

        var (r, quadrant) = Reduce(x);

        return (quadrant & 3) switch
        {
            0 => KernelSin(r),
            1 => KernelCos(r),
            2 => -KernelSin(r),
            _ => -KernelCos(r),
        };
    }

    static double CosD(double x)
    {
        if (!double.IsFinite(x))
        {
            return double.NaN;
        }

        var (r, quadrant) = Reduce(x);

        return (quadrant & 3) switch
        {
            0 => KernelCos(r),
            1 => -KernelSin(r),
            2 => -KernelCos(r),
            _ => KernelSin(r),
        };
    }

    // x = quadrant * pi/2 + r, with r within about pi/4. The pi/2 is split in three so the subtraction stays accurate.
    static (double R, long Quadrant) Reduce(double x)
    {
        var k = Math.Round(x * (2.0 / Math.PI));
        var r = ((x - k * PiOver2Hi) - k * PiOver2Mid) - k * PiOver2Lo;

        return (r, (long)k);
    }

    static double KernelSin(double x)
    {
        const double s1 = -1.66666666666666324348e-01;
        const double s2 = 8.33333333332248946124e-03;
        const double s3 = -1.98412698298579493134e-04;
        const double s4 = 2.75573137070700676789e-06;
        const double s5 = -2.50507602534068634195e-08;
        const double s6 = 1.58969099521155010221e-10;

        var z = x * x;

        return x + x * z * (s1 + z * (s2 + z * (s3 + z * (s4 + z * (s5 + z * s6)))));
    }

    static double KernelCos(double x)
    {
        const double c1 = 4.16666666666666019037e-02;
        const double c2 = -1.38888888888741095749e-03;
        const double c3 = 2.48015872894767294178e-05;
        const double c4 = -2.75573143513906633035e-07;
        const double c5 = 2.08757232129817482790e-09;
        const double c6 = -1.13596475577881948265e-11;

        var z = x * x;

        return 1.0 - 0.5 * z + z * z * (c1 + z * (c2 + z * (c3 + z * (c4 + z * (c5 + z * c6)))));
    }

    static double ExpD(double x)
    {
        if (double.IsNaN(x))
        {
            return double.NaN;
        }

        if (x > 709.0)
        {
            return double.PositiveInfinity;
        }

        if (x < -745.0)
        {
            return 0.0;
        }

        var k = Math.Round(x / Ln2);
        var r = (x - k * Ln2Hi) - k * Ln2Lo;

        // Taylor series for |r| <= ln2 / 2, which has converged long before the 15th term.
        var sum = 1.0;

        for (var n = 15; n >= 1; n--)
        {
            sum = 1.0 + sum * r / n;
        }

        return Math.ScaleB(sum, (int)k);
    }

    static double LogD(double x)
    {
        if (double.IsNaN(x) || x < 0.0)
        {
            return double.NaN;
        }

        if (x == 0.0)
        {
            return double.NegativeInfinity;
        }

        if (double.IsPositiveInfinity(x))
        {
            return x;
        }

        // x = m * 2^e with m in [sqrt(1/2), sqrt(2)).
        var e = 0;

        if (x < 2.2250738585072014e-308)
        {
            x *= 18014398509481984.0;
            e -= 54;
        }

        var bits = BitConverter.DoubleToInt64Bits(x);

        e += (int)((bits >> 52) & 0x7ff) - 1023;

        var m = BitConverter.Int64BitsToDouble((bits & 0x000fffffffffffffL) | 0x3ff0000000000000L);

        if (m > 1.4142135623730951)
        {
            m *= 0.5;
            e++;
        }

        // log(m) = 2 * atanh(s), s = (m - 1) / (m + 1), and |s| < 0.172.
        var s = (m - 1.0) / (m + 1.0);
        var s2 = s * s;
        var sum = 0.0;

        for (var n = 25; n >= 1; n -= 2)
        {
            sum = 1.0 / n + sum * s2;
        }

        return e * Ln2 + 2.0 * s * sum;
    }

    static double PowD(double x, double y)
    {
        if (y == 0.0)
        {
            return 1.0;
        }

        if (double.IsNaN(x) || double.IsNaN(y))
        {
            return double.NaN;
        }

        if (x == 0.0)
        {
            return y > 0.0 ? 0.0 : double.PositiveInfinity;
        }

        if (x < 0.0)
        {
            // Only a whole-number power of a negative number is defined.
            if (y != Math.Floor(y))
            {
                return double.NaN;
            }

            var magnitude = ExpD(y * LogD(-x));

            return Math.IEEERemainder(y, 2.0) != 0.0 ? -magnitude : magnitude;
        }

        return ExpD(y * LogD(x));
    }

    static double Atan2D(double y, double x)
    {
        if (double.IsNaN(x) || double.IsNaN(y))
        {
            return double.NaN;
        }

        if (y == 0.0)
        {
            return double.IsNegative(x) ? (double.IsNegative(y) ? -Math.PI : Math.PI) : y;
        }

        if (x == 0.0)
        {
            return y > 0.0 ? Math.PI / 2 : -Math.PI / 2;
        }

        var ax = Math.Abs(x);
        var ay = Math.Abs(y);
        var angle = AtanUnit(Math.Min(ax, ay) / Math.Max(ax, ay));

        if (ay > ax)
        {
            angle = Math.PI / 2 - angle;
        }

        if (x < 0.0)
        {
            angle = Math.PI - angle;
        }

        return y < 0.0 ? -angle : angle;
    }

    // atan of t in 0..1. Past tan(pi/8) it uses atan(t) = pi/4 + atan((t - 1) / (t + 1)), which keeps the series short.
    static double AtanUnit(double t)
    {
        var offset = 0.0;

        if (t > 0.41421356237309503)
        {
            t = (t - 1.0) / (t + 1.0);
            offset = Math.PI / 4;
        }

        var z = t * t;
        var sum = 0.0;

        // Series t - t^3/3 + t^5/5 - ..., worked from the last term inwards.
        for (var n = 31; n >= 1; n -= 2)
        {
            sum = 1.0 / n - z * sum;
        }

        return offset + t * sum;
    }
}
