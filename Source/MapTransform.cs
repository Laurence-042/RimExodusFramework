using System;
using UnityEngine;
using Verse;

namespace RimExodus.Maps
{
    /// <summary>A frozen display transform. Physical/world coordinates remain the caller's responsibility.</summary>
    public readonly struct MapTransform
    {
        public readonly Matrix4x4 Matrix;
        public readonly Matrix4x4 Inverse;
        // Translation-only consumers need no matrix construction or inverse in a per-Thing hot path.
        public static Vector3 Translate(Vector3 local, IntVec3 offset)
            => new Vector3(local.x + offset.x, local.y, local.z + offset.z);
        public static IntVec3 Translate(IntVec3 local, IntVec3 offset) => local + offset;
        public MapTransform(Matrix4x4 matrix)
        {
            for (int row = 0; row < 4; row++)
                for (int col = 0; col < 4; col++)
                    if (float.IsNaN(matrix[row, col]) || float.IsInfinity(matrix[row, col]))
                        throw new ArgumentException("Map transform must be finite.", nameof(matrix));
            if (matrix.m30 != 0 || matrix.m31 != 0 || matrix.m32 != 0 || matrix.m33 != 1)
                throw new ArgumentException("Map transform must be affine.", nameof(matrix));
            float determinant = matrix.m00 * (matrix.m11 * matrix.m22 - matrix.m12 * matrix.m21)
                - matrix.m01 * (matrix.m10 * matrix.m22 - matrix.m12 * matrix.m20)
                + matrix.m02 * (matrix.m10 * matrix.m21 - matrix.m11 * matrix.m20);
            if (float.IsNaN(determinant) || float.IsInfinity(determinant) || Math.Abs(determinant) < 1e-8f)
                throw new ArgumentException("Map transform must be invertible.", nameof(matrix));
            Matrix = matrix;
            Inverse = matrix.inverse;
        }
        public Vector3 ToDisplay(Vector3 local) => Matrix.MultiplyPoint3x4(local);
        public Vector3 ToLocal(Vector3 display) => Inverse.MultiplyPoint3x4(display);
        public Vector3 DirectionToDisplay(Vector3 local) => Matrix.MultiplyVector(local);
        /// <summary>Conservative display-space AABB of a source-local box, including rotation/scale.
        /// Only for visibility tests; this is not a grid ownership or collision query.</summary>
        public void BoundsToDisplay(Vector3 min, Vector3 max, out Vector3 displayMin, out Vector3 displayMax)
        {
            Vector3 center = ToDisplay((min + max) * 0.5f);
            Vector3 extent = (max - min) * 0.5f;
            var radius = new Vector3(
                Mathf.Abs(Matrix.m00) * extent.x + Mathf.Abs(Matrix.m01) * extent.y + Mathf.Abs(Matrix.m02) * extent.z,
                Mathf.Abs(Matrix.m10) * extent.x + Mathf.Abs(Matrix.m11) * extent.y + Mathf.Abs(Matrix.m12) * extent.z,
                Mathf.Abs(Matrix.m20) * extent.x + Mathf.Abs(Matrix.m21) * extent.y + Mathf.Abs(Matrix.m22) * extent.z);
            displayMin = center - radius;
            displayMax = center + radius;
        }
        public Vector3 ConvertPointTo(MapTransform destination, Vector3 local) => destination.ToLocal(ToDisplay(local));
        public Vector3 ConvertDirectionTo(MapTransform destination, Vector3 local)
            => destination.Inverse.MultiplyVector(DirectionToDisplay(local));
        public IntVec3 CellAt(Vector3 display)
        {
            Vector3 local = ToLocal(display);
            return new IntVec3(Mathf.FloorToInt(local.x), 0, Mathf.FloorToInt(local.z));
        }
        public CellRect LocalView(CellRect view)
        {
            Vector3 a = ToLocal(new Vector3(view.minX, 0, view.minZ));
            Vector3 b = ToLocal(new Vector3(view.maxX + 1, 0, view.minZ));
            Vector3 c = ToLocal(new Vector3(view.minX, 0, view.maxZ + 1));
            Vector3 d = ToLocal(new Vector3(view.maxX + 1, 0, view.maxZ + 1));
            int x = Mathf.FloorToInt(Mathf.Min(Mathf.Min(a.x, b.x), Mathf.Min(c.x, d.x)));
            int z = Mathf.FloorToInt(Mathf.Min(Mathf.Min(a.z, b.z), Mathf.Min(c.z, d.z)));
            int maxX = Mathf.CeilToInt(Mathf.Max(Mathf.Max(a.x, b.x), Mathf.Max(c.x, d.x)));
            int maxZ = Mathf.CeilToInt(Mathf.Max(Mathf.Max(a.z, b.z), Mathf.Max(c.z, d.z)));
            return new CellRect(x, z, maxX - x + 1, maxZ - z + 1);
        }
    }
}
