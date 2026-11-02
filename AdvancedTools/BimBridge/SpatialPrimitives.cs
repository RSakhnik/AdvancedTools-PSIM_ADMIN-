using Ascon.Pilot.Bim.SDK;
using System;
using System.Linq;
namespace AdvancedTools.BimBridge
{
    public static class SpatialMath
    {
        public static bool Finite(double x) => !double.IsNaN(x) && !double.IsInfinity(x);
        public static void ValidateMatrix(double[] m)
        {
            if (m == null || m.Length != 16 || m.Any(x => !Finite(x)))
                throw new BimBridgeException("INVALID_SPATIAL_DATA", "A placement must contain 16 finite values.");
        }
        public static void ValidateAffine(double[] m)
        {
            ValidateMatrix(m);
            if (Math.Abs(m[12]) > 1e-12 || Math.Abs(m[13]) > 1e-12 || Math.Abs(m[14]) > 1e-12 || Math.Abs(m[15]-1) > 1e-12)
                throw new BimBridgeException("INVALID_SPATIAL_DATA", "Non-affine mesh placement is unsupported.");
            var det = m[0]*(m[5]*m[10]-m[6]*m[9])-m[1]*(m[4]*m[10]-m[6]*m[8])+m[2]*(m[4]*m[9]-m[5]*m[8]);
            if (!Finite(det) || det == 0) throw new BimBridgeException("INVALID_SPATIAL_DATA", "Mesh placement is singular.");
        }
        public static double[] Transform(double[] m, double[] p)
        {
            var v = new[] { m[0]*p[0]+m[1]*p[1]+m[2]*p[2]+m[3],
                m[4]*p[0]+m[5]*p[1]+m[6]*p[2]+m[7], m[8]*p[0]+m[9]*p[1]+m[10]*p[2]+m[11] };
            if (v.Any(x => !Finite(x))) throw new BimBridgeException("INVALID_SPATIAL_DATA", "Non-finite coordinate.");
            return v;
        }
        public static double[] Matrix(Matrix4x4 m) => new[] {m.M11,m.M12,m.M13,m.M14,m.M21,m.M22,m.M23,m.M24,
            m.M31,m.M32,m.M33,m.M34,m.M41,m.M42,m.M43,m.M44};
    }

    public sealed class SpatialBounds
    {
        public double[] Min { get; private set; }
        public double[] Max { get; private set; }
        public double[] Center => Min == null ? null : Min.Zip(Max, (a,b) => a/2+b/2).ToArray();
        public void Add(double[] p)
        {
            if (p.Length != 3 || p.Any(x => !SpatialMath.Finite(x))) throw new BimBridgeException("INVALID_SPATIAL_DATA", "Non-finite vertex.");
            if (Min == null) { Min = (double[])p.Clone(); Max = (double[])p.Clone(); return; }
            for (var i=0;i<3;i++) { Min[i]=Math.Min(Min[i],p[i]); Max[i]=Math.Max(Max[i],p[i]); }
        }
        public void Union(SpatialBounds bounds) { if (bounds.Min != null) { Add(bounds.Min); Add(bounds.Max); } }
    }

}
