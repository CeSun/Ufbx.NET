// Math value types and math utility operations ported from ufbx v0.23.1 (ufbx.h + ufbx.c).
//
// C `ufbx_real` defaults to `double` (UFBX_REAL_TYPE defaults to double unless
// UFBX_REAL_IS_FLOAT is defined), so every `ufbx_real` is `double` here.
//
// The math operations below are the functions declared in ufbx.h (implemented in
// ufbx.c, e.g. ufbx_quat_mul, ufbx_matrix_mul, ufbx_transform_to_matrix, ...) plus
// the `ufbxi_*` inline arithmetic helpers from ufbx.c that the rest of the port
// reuses. Bodies are ported verbatim (double path, UFBX_REAL_IS_FLOAT not defined);
// only the C null-pointer fallbacks are dropped since C# structs cannot be null.
//
// Floating point functions must go through `UfbxMath` (port of extra/ufbx_math.c),
// never System.Math (see PORTING_NOTES.md).

namespace Ufbx.NET
{
    // C: UFBX_EPSILON and degree/radian constants from ufbx.c
    internal static class UfbxMathConsts
    {
        // C: UFBX_EPSILON (double configuration: 1.4916681462400413e-154;
        // float configuration would be 1.0842021795674597e-19f)
        internal const double Epsilon = 1.4916681462400413e-154;

        // C: UFBXI_DPI
        internal const double DPi = 3.14159265358979323846;

        // C: UFBXI_DEG_TO_RAD_DOUBLE
        internal const double DegToRad = DPi / 180.0;

        // C: UFBXI_RAD_TO_DEG_DOUBLE
        internal const double RadToDeg = 180.0 / DPi;
    }

    // 2D vector (C: ufbx_vec2)
    // C union { struct { x, y; }; v[2]; } -> X/Y fields (V[i] == (X, Y)[i])
    public struct UfbxVec2
    {
        public double X; // C: x
        public double Y; // C: y

        public UfbxVec2(double x, double y)
        {
            X = x;
            Y = y;
        }

        // C: ufbxi_distsq2 — squared distance between two points
        internal static double DistSq2(UfbxVec2 a, UfbxVec2 b)
        {
            double dx = a.X - b.X, dy = a.Y - b.Y;
            return dx * dx + dy * dy;
        }
    }

    // 3D vector (C: ufbx_vec3)
    // C union { struct { x, y, z; }; v[3]; } -> X/Y/Z fields (V[i] == (X, Y, Z)[i])
    public struct UfbxVec3
    {
        public double X; // C: x
        public double Y; // C: y
        public double Z; // C: z

        public UfbxVec3(double x, double y, double z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        // Zero vector (C: ufbx_zero_vec3 = { 0,0,0 })
        public static readonly UfbxVec3 Zero = default;

        // C: ufbx_vec3_normalize — vector math utility function.
        // Body is ufbxi_normalize3(): len = sqrt(dot(a,a)), scale by 1/len if above UFBX_EPSILON.
        public static UfbxVec3 Normalize(UfbxVec3 v)
        {
            return Normalize3(v);
        }

        // C: ufbxi_add3
        internal static UfbxVec3 Add3(UfbxVec3 a, UfbxVec3 b)
        {
            return new UfbxVec3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        }

        // C: ufbxi_sub3
        internal static UfbxVec3 Sub3(UfbxVec3 a, UfbxVec3 b)
        {
            return new UfbxVec3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        }

        // C: ufbxi_mul3
        internal static UfbxVec3 Mul3(UfbxVec3 a, double b)
        {
            return new UfbxVec3(a.X * b, a.Y * b, a.Z * b);
        }

        // C: ufbxi_lerp3
        internal static UfbxVec3 Lerp3(UfbxVec3 a, UfbxVec3 b, double t)
        {
            double u = 1.0 - t;
            return new UfbxVec3(a.X * u + b.X * t, a.Y * u + b.Y * t, a.Z * u + b.Z * t);
        }

        // C: ufbxi_dot3
        internal static double Dot3(UfbxVec3 a, UfbxVec3 b)
        {
            return a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        }

        // C: ufbxi_length3
        internal static double Length3(UfbxVec3 v)
        {
            return UfbxMath.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
        }

        // C: ufbxi_min3
        internal static double Min3(UfbxVec3 v)
        {
            return UfbxMath.FMin(UfbxMath.FMin(v.X, v.Y), v.Z);
        }

        // C: ufbxi_cross3
        internal static UfbxVec3 Cross3(UfbxVec3 a, UfbxVec3 b)
        {
            return new UfbxVec3(
                a.Y * b.Z - a.Z * b.Y,
                a.Z * b.X - a.X * b.Z,
                a.X * b.Y - a.Y * b.X);
        }

        // C: ufbxi_normalize3
        internal static UfbxVec3 Normalize3(UfbxVec3 a)
        {
            double len = UfbxMath.Sqrt(Dot3(a, a));
            if (len > UfbxMathConsts.Epsilon)
            {
                return Mul3(a, 1.0 / len);
            }
            else
            {
                return default;
            }
        }

        // C: ufbxi_neg3
        internal static UfbxVec3 Neg3(UfbxVec3 a)
        {
            return new UfbxVec3(-a.X, -a.Y, -a.Z);
        }
    }

    // 4D vector (C: ufbx_vec4)
    // C union { struct { x, y, z, w; }; v[4]; } -> X/Y/Z/W fields (V[i] == (X, Y, Z, W)[i])
    public struct UfbxVec4
    {
        public double X; // C: x
        public double Y; // C: y
        public double Z; // C: z
        public double W; // C: w

        public UfbxVec4(double x, double y, double z, double w)
        {
            X = x;
            Y = y;
            Z = z;
            W = w;
        }

        // Zero vector (C: ufbx_zero_vec4 = { 0,0,0,0 })
        public static readonly UfbxVec4 Zero = default;
    }

    // Quaternion (C: ufbx_quat)
    // C union { struct { x, y, z, w; }; v[4]; } -> X/Y/Z/W fields (V[i] == (X, Y, Z, W)[i])
    public struct UfbxQuat
    {
        public double X; // C: x
        public double Y; // C: y
        public double Z; // C: z
        public double W; // C: w

        public UfbxQuat(double x, double y, double z, double w)
        {
            X = x;
            Y = y;
            Z = z;
            W = w;
        }

        // Identity quaternion (C: ufbx_identity_quat = { 0,0,0,1 })
        public static readonly UfbxQuat Identity = new UfbxQuat(0.0, 0.0, 0.0, 1.0);

        // C: ufbx_quat_dot
        public static double Dot(UfbxQuat a, UfbxQuat b)
        {
            return a.X * b.X + a.Y * b.Y + a.Z * b.Z + a.W * b.W;
        }

        // C: ufbx_quat_mul (body from ufbxi_mul_quat)
        public static UfbxQuat Mul(UfbxQuat a, UfbxQuat b)
        {
            UfbxQuat r;
            r.X = a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y;
            r.Y = a.W * b.Y - a.X * b.Z + a.Y * b.W + a.Z * b.X;
            r.Z = a.W * b.Z + a.X * b.Y - a.Y * b.X + a.Z * b.W;
            r.W = a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z;
            return r;
        }

        // C: ufbx_quat_normalize
        public static UfbxQuat Normalize(UfbxQuat q)
        {
            double norm = Dot(q, q);
            if (norm == 0.0) return Identity;
            norm = UfbxMath.Sqrt(norm);
            q.X /= norm;
            q.Y /= norm;
            q.Z /= norm;
            q.W /= norm;
            return q;
        }

        // C: ufbx_quat_fix_antipodal
        public static UfbxQuat FixAntipodal(UfbxQuat q, UfbxQuat reference)
        {
            if (Dot(q, reference) < 0.0)
            {
                q.X = -q.X; q.Y = -q.Y; q.Z = -q.Z; q.W = -q.W;
            }
            return q;
        }

        // C: ufbx_quat_slerp
        public static UfbxQuat Slerp(UfbxQuat a, UfbxQuat b, double t)
        {
            double dot = a.X * b.X + a.Y * b.Y + a.Z * b.Z + a.W * b.W;
            if (dot < 0.0)
            {
                dot = -dot;
                b.X = -b.X; b.Y = -b.Y; b.Z = -b.Z; b.W = -b.W;
            }
            double omega = UfbxMath.Acos(UfbxMath.FMin(UfbxMath.FMax(dot, 0.0), 1.0));
            if (omega <= 1.175494351e-38f) return a;
            double rcp_so = 1.0 / UfbxMath.Sin(omega);
            double af = UfbxMath.Sin((1.0 - t) * omega) * rcp_so;
            double bf = UfbxMath.Sin(t * omega) * rcp_so;

            double x = af * a.X + bf * b.X;
            double y = af * a.Y + bf * b.Y;
            double z = af * a.Z + bf * b.Z;
            double w = af * a.W + bf * b.W;
            double rcp_len = 1.0 / UfbxMath.Sqrt(x * x + y * y + z * z + w * w);

            UfbxQuat ret;
            ret.X = x * rcp_len;
            ret.Y = y * rcp_len;
            ret.Z = z * rcp_len;
            ret.W = w * rcp_len;
            return ret;
        }

        // C: ufbx_quat_rotate_vec3
        public static UfbxVec3 RotateVec3(UfbxQuat q, UfbxVec3 v)
        {
            double xy = q.X * v.Y - q.Y * v.X;
            double xz = q.X * v.Z - q.Z * v.X;
            double yz = q.Y * v.Z - q.Z * v.Y;
            UfbxVec3 r;
            r.X = 2.0 * (+q.W * yz + q.Y * xy + q.Z * xz) + v.X;
            r.Y = 2.0 * (-q.X * xy - q.W * xz + q.Z * yz) + v.Y;
            r.Z = 2.0 * (-q.X * xz - q.Y * yz + q.W * xy) + v.Z;
            return r;
        }

        // C: ufbx_quat_to_euler
        // TODO from C source: derive these rigorously
        public static UfbxVec3 ToEuler(UfbxQuat q, UfbxRotationOrder order)
        {
            // C: const double eps = 0.999999999 (double configuration)
            const double eps = 0.999999999;

            double vx, vy, vz;
            double t;

            double qx = q.X, qy = q.Y, qz = q.Z, qw = q.W;

            // Generated by `misc/gen_quat_to_euler.py` (C source)
            switch (order)
            {
                case UfbxRotationOrder.Xyz:
                    t = 2.0 * (qw * qy - qx * qz);
                    if (UfbxMath.Abs(t) < eps)
                    {
                        vy = UfbxMath.Asin(t);
                        vz = UfbxMath.Atan2(2.0 * (qw * qz + qx * qy), 2.0 * (qw * qw + qx * qx) - 1.0);
                        vx = -UfbxMath.Atan2(-2.0 * (qw * qx + qy * qz), 2.0 * (qw * qw + qz * qz) - 1.0);
                    }
                    else
                    {
                        vy = UfbxMath.CopySign(UfbxMathConsts.DPi * 0.5, t);
                        vz = UfbxMath.Atan2(-2.0 * t * (qw * qx - qy * qz), t * (2.0 * qw * qy + 2.0 * qx * qz));
                        vx = 0.0;
                    }
                    break;
                case UfbxRotationOrder.Xzy:
                    t = 2.0 * (qw * qz + qx * qy);
                    if (UfbxMath.Abs(t) < eps)
                    {
                        vz = UfbxMath.Asin(t);
                        vy = UfbxMath.Atan2(2.0 * (qw * qy - qx * qz), 2.0 * (qw * qw + qx * qx) - 1.0);
                        vx = -UfbxMath.Atan2(-2.0 * (qw * qx - qy * qz), 2.0 * (qw * qw + qy * qy) - 1.0);
                    }
                    else
                    {
                        vz = UfbxMath.CopySign(UfbxMathConsts.DPi * 0.5, t);
                        vy = UfbxMath.Atan2(2.0 * t * (qw * qx + qy * qz), -t * (2.0 * qx * qy - 2.0 * qw * qz));
                        vx = 0.0;
                    }
                    break;
                case UfbxRotationOrder.Yzx:
                    t = 2.0 * (qw * qz - qx * qy);
                    if (UfbxMath.Abs(t) < eps)
                    {
                        vz = UfbxMath.Asin(t);
                        vx = UfbxMath.Atan2(2.0 * (qw * qx + qy * qz), 2.0 * (qw * qw + qy * qy) - 1.0);
                        vy = -UfbxMath.Atan2(-2.0 * (qw * qy + qx * qz), 2.0 * (qw * qw + qx * qx) - 1.0);
                    }
                    else
                    {
                        vz = UfbxMath.CopySign(UfbxMathConsts.DPi * 0.5, t);
                        vx = UfbxMath.Atan2(-2.0 * t * (qw * qy - qx * qz), t * (2.0 * qw * qz + 2.0 * qx * qy));
                        vy = 0.0;
                    }
                    break;
                case UfbxRotationOrder.Yxz:
                    t = 2.0 * (qw * qx + qy * qz);
                    if (UfbxMath.Abs(t) < eps)
                    {
                        vx = UfbxMath.Asin(t);
                        vz = UfbxMath.Atan2(2.0 * (qw * qz - qx * qy), 2.0 * (qw * qw + qy * qy) - 1.0);
                        vy = -UfbxMath.Atan2(-2.0 * (qw * qy - qx * qz), 2.0 * (qw * qw + qz * qz) - 1.0);
                    }
                    else
                    {
                        vx = UfbxMath.CopySign(UfbxMathConsts.DPi * 0.5, t);
                        vz = UfbxMath.Atan2(2.0 * t * (qw * qy + qx * qz), -t * (2.0 * qy * qz - 2.0 * qw * qx));
                        vy = 0.0;
                    }
                    break;
                case UfbxRotationOrder.Zxy:
                    t = 2.0 * (qw * qx - qy * qz);
                    if (UfbxMath.Abs(t) < eps)
                    {
                        vx = UfbxMath.Asin(t);
                        vy = UfbxMath.Atan2(2.0 * (qw * qy + qx * qz), 2.0 * (qw * qw + qz * qz) - 1.0);
                        vz = -UfbxMath.Atan2(-2.0 * (qw * qz + qx * qy), 2.0 * (qw * qw + qy * qy) - 1.0);
                    }
                    else
                    {
                        vx = UfbxMath.CopySign(UfbxMathConsts.DPi * 0.5, t);
                        vy = UfbxMath.Atan2(-2.0 * t * (qw * qz - qx * qy), t * (2.0 * qw * qx + 2.0 * qy * qz));
                        vz = 0.0;
                    }
                    break;
                case UfbxRotationOrder.Zyx:
                    t = 2.0 * (qw * qy + qx * qz);
                    if (UfbxMath.Abs(t) < eps)
                    {
                        vy = UfbxMath.Asin(t);
                        vx = UfbxMath.Atan2(2.0 * (qw * qx - qy * qz), 2.0 * (qw * qw + qz * qz) - 1.0);
                        vz = -UfbxMath.Atan2(-2.0 * (qw * qz - qx * qy), 2.0 * (qw * qw + qx * qx) - 1.0);
                    }
                    else
                    {
                        vy = UfbxMath.CopySign(UfbxMathConsts.DPi * 0.5, t);
                        vx = UfbxMath.Atan2(2.0 * t * (qw * qz + qx * qy), -t * (2.0 * qx * qz - 2.0 * qw * qy));
                        vz = 0.0;
                    }
                    break;
                default:
                    vx = vy = vz = 0.0;
                    break;
            }

            vx *= UfbxMathConsts.RadToDeg;
            vy *= UfbxMathConsts.RadToDeg;
            vz *= UfbxMathConsts.RadToDeg;

            return new UfbxVec3(vx, vy, vz);
        }

        // C: ufbx_euler_to_quat
        public static UfbxQuat EulerToQuat(UfbxVec3 v, UfbxRotationOrder order)
        {
            double vx = v.X * (UfbxMathConsts.DegToRad * 0.5);
            double vy = v.Y * (UfbxMathConsts.DegToRad * 0.5);
            double vz = v.Z * (UfbxMathConsts.DegToRad * 0.5);
            double cx = UfbxMath.Cos(vx), sx = UfbxMath.Sin(vx);
            double cy = UfbxMath.Cos(vy), sy = UfbxMath.Sin(vy);
            double cz = UfbxMath.Cos(vz), sz = UfbxMath.Sin(vz);
            UfbxQuat q;

            // Generated by `misc/gen_rotation_order.py` (C source)
            switch (order)
            {
                case UfbxRotationOrder.Xyz:
                    q.X = -cx * sy * sz + cy * cz * sx;
                    q.Y = cx * cz * sy + cy * sx * sz;
                    q.Z = cx * cy * sz - cz * sx * sy;
                    q.W = cx * cy * cz + sx * sy * sz;
                    break;
                case UfbxRotationOrder.Xzy:
                    q.X = cx * sy * sz + cy * cz * sx;
                    q.Y = cx * cz * sy + cy * sx * sz;
                    q.Z = cx * cy * sz - cz * sx * sy;
                    q.W = cx * cy * cz - sx * sy * sz;
                    break;
                case UfbxRotationOrder.Yzx:
                    q.X = -cx * sy * sz + cy * cz * sx;
                    q.Y = cx * cz * sy - cy * sx * sz;
                    q.Z = cx * cy * sz + cz * sx * sy;
                    q.W = cx * cy * cz + sx * sy * sz;
                    break;
                case UfbxRotationOrder.Yxz:
                    q.X = -cx * sy * sz + cy * cz * sx;
                    q.Y = cx * cz * sy + cy * sx * sz;
                    q.Z = cx * cy * sz + cz * sx * sy;
                    q.W = cx * cy * cz - sx * sy * sz;
                    break;
                case UfbxRotationOrder.Zxy:
                    q.X = cx * sy * sz + cy * cz * sx;
                    q.Y = cx * cz * sy - cy * sx * sz;
                    q.Z = cx * cy * sz - cz * sx * sy;
                    q.W = cx * cy * cz + sx * sy * sz;
                    break;
                case UfbxRotationOrder.Zyx:
                    q.X = cx * sy * sz + cy * cz * sx;
                    q.Y = cx * cz * sy - cy * sx * sz;
                    q.Z = cx * cy * sz + cz * sx * sy;
                    q.W = cx * cy * cz - sx * sy * sz;
                    break;
                default:
                    q.X = q.Y = q.Z = 0.0; q.W = 1.0;
                    break;
            }

            return q;
        }
    }

    // Explicit translation+rotation+scale transformation (C: ufbx_transform)
    // NOTE from C header: rotation is a quaternion, not Euler angles!
    public struct UfbxTransform
    {
        public UfbxVec3 Translation; // C: translation
        public UfbxQuat Rotation;    // C: rotation
        public UfbxVec3 Scale;       // C: scale

        public UfbxTransform(UfbxVec3 translation, UfbxQuat rotation, UfbxVec3 scale)
        {
            Translation = translation;
            Rotation = rotation;
            Scale = scale;
        }

        // Identity transform (C: ufbx_identity_transform = { {0,0,0}, {0,0,0,1}, {1,1,1} })
        public static readonly UfbxTransform Identity = new UfbxTransform(
            UfbxVec3.Zero, UfbxQuat.Identity, new UfbxVec3(1.0, 1.0, 1.0));
    }

    // 4x3 matrix encoding an affine transformation (C: ufbx_matrix)
    // `cols[0..2]` are the X/Y/Z basis vectors, `cols[3]` is the translation.
    // C union { struct { m00,m10,m20, m01,m11,m21, m02,m12,m22, m03,m13,m23 }; cols[4]; v[12]; }
    // -> scalar fields in the same memory order; `Cols(i)` reconstructs the column view.
    public struct UfbxMatrix
    {
        public double M00; // C: m00
        public double M10; // C: m10
        public double M20; // C: m20
        public double M01; // C: m01
        public double M11; // C: m11
        public double M21; // C: m21
        public double M02; // C: m02
        public double M12; // C: m12
        public double M22; // C: m22
        public double M03; // C: m03
        public double M13; // C: m13
        public double M23; // C: m23

        // Identity matrix (C: ufbx_identity_matrix = { 1,0,0, 0,1,0, 0,0,1, 0,0,0 })
        public static readonly UfbxMatrix Identity = new UfbxMatrix
        {
            M00 = 1.0,
            M11 = 1.0,
            M22 = 1.0,
        };

        // C: cols[4] — column view over the scalar storage.
        // cols[0] = (m00, m10, m20), cols[1] = (m01, m11, m21),
        // cols[2] = (m02, m12, m22), cols[3] = (m03, m13, m23)
        public UfbxVec3 GetCol(int col)
        {
            switch (col)
            {
                case 0: return new UfbxVec3(M00, M10, M20);
                case 1: return new UfbxVec3(M01, M11, M21);
                case 2: return new UfbxVec3(M02, M12, M22);
                default: return new UfbxVec3(M03, M13, M23);
            }
        }

        // C: ufbx_matrix_mul
        public static UfbxMatrix Mul(UfbxMatrix a, UfbxMatrix b)
        {
            UfbxMatrix dst;

            dst.M03 = a.M00 * b.M03 + a.M01 * b.M13 + a.M02 * b.M23 + a.M03;
            dst.M13 = a.M10 * b.M03 + a.M11 * b.M13 + a.M12 * b.M23 + a.M13;
            dst.M23 = a.M20 * b.M03 + a.M21 * b.M13 + a.M22 * b.M23 + a.M23;

            dst.M00 = a.M00 * b.M00 + a.M01 * b.M10 + a.M02 * b.M20;
            dst.M10 = a.M10 * b.M00 + a.M11 * b.M10 + a.M12 * b.M20;
            dst.M20 = a.M20 * b.M00 + a.M21 * b.M10 + a.M22 * b.M20;

            dst.M01 = a.M00 * b.M01 + a.M01 * b.M11 + a.M02 * b.M21;
            dst.M11 = a.M10 * b.M01 + a.M11 * b.M11 + a.M12 * b.M21;
            dst.M21 = a.M20 * b.M01 + a.M21 * b.M11 + a.M22 * b.M21;

            dst.M02 = a.M00 * b.M02 + a.M01 * b.M12 + a.M02 * b.M22;
            dst.M12 = a.M10 * b.M02 + a.M11 * b.M12 + a.M12 * b.M22;
            dst.M22 = a.M20 * b.M02 + a.M21 * b.M12 + a.M22 * b.M22;

            return dst;
        }

        // C: ufbx_matrix_determinant
        public static double Determinant(UfbxMatrix m)
        {
            return
                -m.M02 * m.M11 * m.M20 + m.M01 * m.M12 * m.M20 + m.M02 * m.M10 * m.M21
                - m.M00 * m.M12 * m.M21 - m.M01 * m.M10 * m.M22 + m.M00 * m.M11 * m.M22;
        }

        // C: ufbx_matrix_invert
        public static UfbxMatrix Invert(UfbxMatrix m)
        {
            double det = Determinant(m);

            UfbxMatrix r;
            if (UfbxMath.Abs(det) <= UfbxMathConsts.Epsilon)
            {
                // C: memset(&r, 0, sizeof(r))
                return default;
            }

            double rcp_det = 1.0 / det;

            r.M00 = (-m.M12 * m.M21 + m.M11 * m.M22) * rcp_det;
            r.M10 = (+m.M12 * m.M20 - m.M10 * m.M22) * rcp_det;
            r.M20 = (-m.M11 * m.M20 + m.M10 * m.M21) * rcp_det;
            r.M01 = (+m.M02 * m.M21 - m.M01 * m.M22) * rcp_det;
            r.M11 = (-m.M02 * m.M20 + m.M00 * m.M22) * rcp_det;
            r.M21 = (+m.M01 * m.M20 - m.M00 * m.M21) * rcp_det;
            r.M02 = (-m.M02 * m.M11 + m.M01 * m.M12) * rcp_det;
            r.M12 = (+m.M02 * m.M10 - m.M00 * m.M12) * rcp_det;
            r.M22 = (-m.M01 * m.M10 + m.M00 * m.M11) * rcp_det;
            r.M03 = (m.M03 * m.M12 * m.M21 - m.M02 * m.M13 * m.M21 - m.M03 * m.M11 * m.M22 + m.M01 * m.M13 * m.M22 + m.M02 * m.M11 * m.M23 - m.M01 * m.M12 * m.M23) * rcp_det;
            r.M13 = (m.M02 * m.M13 * m.M20 - m.M03 * m.M12 * m.M20 + m.M03 * m.M10 * m.M22 - m.M00 * m.M13 * m.M22 - m.M02 * m.M10 * m.M23 + m.M00 * m.M12 * m.M23) * rcp_det;
            r.M23 = (m.M03 * m.M11 * m.M20 - m.M01 * m.M13 * m.M20 - m.M03 * m.M10 * m.M21 + m.M00 * m.M13 * m.M21 + m.M01 * m.M10 * m.M23 - m.M00 * m.M11 * m.M23) * rcp_det;

            return r;
        }

        // C: ufbx_matrix_for_normals
        // Get a matrix that can be used to transform geometry normals.
        // NOTE from C header: you must normalize the normals after transforming them with
        // this matrix, eg. using `UfbxVec3.Normalize()`. This function flips the normals
        // if the determinant is negative.
        public static UfbxMatrix ForNormals(UfbxMatrix m)
        {
            double det = Determinant(m);
            double det_sign = det >= 0.0 ? 1.0 : -1.0;

            UfbxMatrix r;
            r.M00 = (-m.M12 * m.M21 + m.M11 * m.M22) * det_sign;
            r.M01 = (+m.M12 * m.M20 - m.M10 * m.M22) * det_sign;
            r.M02 = (-m.M11 * m.M20 + m.M10 * m.M21) * det_sign;
            r.M10 = (+m.M02 * m.M21 - m.M01 * m.M22) * det_sign;
            r.M11 = (-m.M02 * m.M20 + m.M00 * m.M22) * det_sign;
            r.M12 = (+m.M01 * m.M20 - m.M00 * m.M21) * det_sign;
            r.M20 = (-m.M02 * m.M11 + m.M01 * m.M12) * det_sign;
            r.M21 = (+m.M02 * m.M10 - m.M00 * m.M12) * det_sign;
            r.M22 = (-m.M01 * m.M10 + m.M00 * m.M11) * det_sign;
            r.M03 = r.M13 = r.M23 = 0.0;

            return r;
        }

        // C: ufbx_transform_position
        public static UfbxVec3 TransformPosition(UfbxMatrix m, UfbxVec3 v)
        {
            UfbxVec3 r;
            r.X = m.M00 * v.X + m.M01 * v.Y + m.M02 * v.Z + m.M03;
            r.Y = m.M10 * v.X + m.M11 * v.Y + m.M12 * v.Z + m.M13;
            r.Z = m.M20 * v.X + m.M21 * v.Y + m.M22 * v.Z + m.M23;
            return r;
        }

        // C: ufbx_transform_direction
        public static UfbxVec3 TransformDirection(UfbxMatrix m, UfbxVec3 v)
        {
            UfbxVec3 r;
            r.X = m.M00 * v.X + m.M01 * v.Y + m.M02 * v.Z;
            r.Y = m.M10 * v.X + m.M11 * v.Y + m.M12 * v.Z;
            r.Z = m.M20 * v.X + m.M21 * v.Y + m.M22 * v.Z;
            return r;
        }

        // C: ufbx_transform_to_matrix
        public static UfbxMatrix FromTransform(UfbxTransform t)
        {
            UfbxQuat q = t.Rotation;
            double sx = 2.0 * t.Scale.X, sy = 2.0 * t.Scale.Y, sz = 2.0 * t.Scale.Z;
            double xx = q.X * q.X, xy = q.X * q.Y, xz = q.X * q.Z, xw = q.X * q.W;
            double yy = q.Y * q.Y, yz = q.Y * q.Z, yw = q.Y * q.W;
            double zz = q.Z * q.Z, zw = q.Z * q.W;
            UfbxMatrix m;
            m.M00 = sx * (-yy - zz + 0.5);
            m.M10 = sx * (+xy + zw);
            m.M20 = sx * (-yw + xz);
            m.M01 = sy * (-zw + xy);
            m.M11 = sy * (-xx - zz + 0.5);
            m.M21 = sy * (+xw + yz);
            m.M02 = sz * (+xz + yw);
            m.M12 = sz * (-xw + yz);
            m.M22 = sz * (-xx - yy + 0.5);
            m.M03 = t.Translation.X;
            m.M13 = t.Translation.Y;
            m.M23 = t.Translation.Z;
            return m;
        }

        // C: ufbx_matrix_to_transform
        public static UfbxTransform ToTransform(UfbxMatrix m)
        {
            double det = Determinant(m);

            UfbxTransform t;
            t.Translation = m.GetCol(3);
            t.Scale.X = UfbxVec3.Length3(m.GetCol(0));
            t.Scale.Y = UfbxVec3.Length3(m.GetCol(1));
            t.Scale.Z = UfbxVec3.Length3(m.GetCol(2));
            t.Rotation = default;

            // Flip a single non-zero axis if negative determinant
            double sign_x = 1.0;
            double sign_y = 1.0;
            double sign_z = 1.0;
            if (det < 0.0)
            {
                if (t.Scale.X > 0.0) sign_x = -1.0;
                else if (t.Scale.Y > 0.0) sign_y = -1.0;
                else if (t.Scale.Z > 0.0) sign_z = -1.0;
            }

            UfbxVec3 x = UfbxVec3.Mul3(m.GetCol(0), t.Scale.X > 0.0 ? sign_x / t.Scale.X : 0.0);
            UfbxVec3 y = UfbxVec3.Mul3(m.GetCol(1), t.Scale.Y > 0.0 ? sign_y / t.Scale.Y : 0.0);
            UfbxVec3 z = UfbxVec3.Mul3(m.GetCol(2), t.Scale.Z > 0.0 ? sign_z / t.Scale.Z : 0.0);
            double trace = x.X + y.Y + z.Z;
            if (trace > 0.0)
            {
                double a = UfbxMath.Sqrt(UfbxMath.FMax(0.0, trace + 1.0)), b = (a != 0.0) ? 0.5 / a : 0.0;
                t.Rotation.X = (y.Z - z.Y) * b;
                t.Rotation.Y = (z.X - x.Z) * b;
                t.Rotation.Z = (x.Y - y.X) * b;
                t.Rotation.W = 0.5 * a;
            }
            else if (x.X > y.Y && x.X > z.Z)
            {
                double a = UfbxMath.Sqrt(UfbxMath.FMax(0.0, 1.0 + x.X - y.Y - z.Z)), b = (a != 0.0) ? 0.5 / a : 0.0;
                t.Rotation.X = 0.5 * a;
                t.Rotation.Y = (y.X + x.Y) * b;
                t.Rotation.Z = (z.X + x.Z) * b;
                t.Rotation.W = (y.Z - z.Y) * b;
            }
            else if (y.Y > z.Z)
            {
                double a = UfbxMath.Sqrt(UfbxMath.FMax(0.0, 1.0 - x.X + y.Y - z.Z)), b = (a != 0.0) ? 0.5 / a : 0.0;
                t.Rotation.X = (y.X + x.Y) * b;
                t.Rotation.Y = 0.5 * a;
                t.Rotation.Z = (z.Y + y.Z) * b;
                t.Rotation.W = (z.X - x.Z) * b;
            }
            else
            {
                double a = UfbxMath.Sqrt(UfbxMath.FMax(0.0, 1.0 - x.X - y.Y + z.Z)), b = (a != 0.0) ? 0.5 / a : 0.0;
                t.Rotation.X = (z.X + x.Z) * b;
                t.Rotation.Y = (z.Y + y.Z) * b;
                t.Rotation.Z = 0.5 * a;
                t.Rotation.W = (x.Y - y.X) * b;
            }

            double len = t.Rotation.X * t.Rotation.X + t.Rotation.Y * t.Rotation.Y + t.Rotation.Z * t.Rotation.Z + t.Rotation.W * t.Rotation.W;
            if (UfbxMath.Abs(len - 1.0) > UfbxMathConsts.Epsilon)
            {
                if (UfbxMath.Abs(len) <= UfbxMathConsts.Epsilon)
                {
                    t.Rotation = UfbxQuat.Identity;
                }
                else
                {
                    t.Rotation.X /= len;
                    t.Rotation.Y /= len;
                    t.Rotation.Z /= len;
                    t.Rotation.W /= len;
                }
            }

            t.Scale.X *= sign_x;
            t.Scale.Y *= sign_y;
            t.Scale.Z *= sign_z;

            return t;
        }
    }

    // Coordinate axes the scene is represented in (C: ufbx_coordinate_axes)
    // NOTE from C header: `front` is the _opposite_ from forward!
    public struct UfbxCoordinateAxes
    {
        public UfbxCoordinateAxis Right; // C: right
        public UfbxCoordinateAxis Up;    // C: up
        public UfbxCoordinateAxis Front; // C: front

        public UfbxCoordinateAxes(UfbxCoordinateAxis right, UfbxCoordinateAxis up, UfbxCoordinateAxis front)
        {
            Right = right;
            Up = up;
            Front = front;
        }

        // Commonly used coordinate axes (C: ufbx_axes_right_handed_y_up etc.)
        public static readonly UfbxCoordinateAxes RightHandedYUp =
            new UfbxCoordinateAxes(UfbxCoordinateAxis.PositiveX, UfbxCoordinateAxis.PositiveY, UfbxCoordinateAxis.PositiveZ);
        public static readonly UfbxCoordinateAxes RightHandedZUp =
            new UfbxCoordinateAxes(UfbxCoordinateAxis.PositiveX, UfbxCoordinateAxis.PositiveZ, UfbxCoordinateAxis.NegativeY);
        public static readonly UfbxCoordinateAxes LeftHandedYUp =
            new UfbxCoordinateAxes(UfbxCoordinateAxis.PositiveX, UfbxCoordinateAxis.PositiveY, UfbxCoordinateAxis.NegativeZ);
        public static readonly UfbxCoordinateAxes LeftHandedZUp =
            new UfbxCoordinateAxes(UfbxCoordinateAxis.PositiveX, UfbxCoordinateAxis.PositiveZ, UfbxCoordinateAxis.PositiveY);

        // C: ufbx_coordinate_axes_valid (ufbx.c:31486-31498)
        // Returns true if `axes` forms a valid coordinate space.
        public static bool IsValid(UfbxCoordinateAxes axes)
        {
            if (axes.Right < UfbxCoordinateAxis.PositiveX || axes.Right > UfbxCoordinateAxis.NegativeZ) return false;
            if (axes.Up < UfbxCoordinateAxis.PositiveX || axes.Up > UfbxCoordinateAxis.NegativeZ) return false;
            if (axes.Front < UfbxCoordinateAxis.PositiveX || axes.Front > UfbxCoordinateAxis.NegativeZ) return false;

            // Check that all the positive/negative axes are used
            uint mask = 0;
            mask |= 1u << ((int)axes.Right >> 1);
            mask |= 1u << ((int)axes.Up >> 1);
            mask |= 1u << ((int)axes.Front >> 1);
            return (mask & 0x7u) == 0x7u;
        }
    }
}
