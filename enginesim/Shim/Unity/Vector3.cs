using System;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace UnityEngine;

public struct Vector3 : IEquatable<Vector3>, IFormattable
{
	public float x;

	public float y;

	public float z;

	private static readonly Vector3 zeroVector = new Vector3(0f, 0f, 0f);

	private static readonly Vector3 oneVector = new Vector3(1f, 1f, 1f);

	private static readonly Vector3 upVector = new Vector3(0f, 1f, 0f);

	public readonly Vector3 normalized
	{
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		get
		{
			return Normalize(this);
		}
	}

	public readonly float magnitude
	{
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		get
		{
			return (float)Math.Sqrt(x * x + y * y + z * z);
		}
	}

	public readonly float sqrMagnitude
	{
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		get
		{
			return x * x + y * y + z * z;
		}
	}

	public static Vector3 zero
	{
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		get
		{
			return zeroVector;
		}
	}

	public static Vector3 one
	{
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		get
		{
			return oneVector;
		}
	}

	public static Vector3 up
	{
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		get
		{
			return upVector;
		}
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static Vector3 Lerp(Vector3 a, Vector3 b, float t)
	{
		t = Mathf.Clamp01(t);
		Vector3 result = default(Vector3);
		result.x = a.x + (b.x - a.x) * t;
		result.y = a.y + (b.y - a.y) * t;
		result.z = a.z + (b.z - a.z) * t;
		return result;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static Vector3 MoveTowards(Vector3 current, Vector3 target, float maxDistanceDelta)
	{
		float num = target.x - current.x;
		float num2 = target.y - current.y;
		float num3 = target.z - current.z;
		float num4 = num * num + num2 * num2 + num3 * num3;
		if (num4 == 0f || (maxDistanceDelta >= 0f && num4 <= maxDistanceDelta * maxDistanceDelta))
		{
			return target;
		}
		float num5 = (float)Math.Sqrt(num4);
		Vector3 result = default(Vector3);
		result.x = current.x + num / num5 * maxDistanceDelta;
		result.y = current.y + num2 / num5 * maxDistanceDelta;
		result.z = current.z + num3 / num5 * maxDistanceDelta;
		return result;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public Vector3(float x, float y, float z)
	{
		this.x = x;
		this.y = y;
		this.z = z;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public Vector3(float x, float y)
	{
		this.x = x;
		this.y = y;
		z = 0f;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static Vector3 Scale(Vector3 a, Vector3 b)
	{
		return new Vector3
		{
			x = a.x * b.x,
			y = a.y * b.y,
			z = a.z * b.z
		};
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public override readonly int GetHashCode()
	{
		return x.GetHashCode() ^ (y.GetHashCode() << 2) ^ (z.GetHashCode() >> 2);
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public override readonly bool Equals(object other)
	{
		if (other is Vector3 other2)
		{
			return Equals(other2);
		}
		return false;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public readonly bool Equals(Vector3 other)
	{
		return x == other.x && y == other.y && z == other.z;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static Vector3 Normalize(Vector3 value)
	{
		float num = value.magnitude;
		return (num > 1E-05f) ? new Vector3
		{
			x = value.x / num,
			y = value.y / num,
			z = value.z / num
		} : zeroVector;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public void Normalize()
	{
		float num = magnitude;
		if (num > 1E-05f)
		{
			x /= num;
			y /= num;
			z /= num;
		}
		else
		{
			x = 0f;
			y = 0f;
			z = 0f;
		}
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static float Dot(Vector3 lhs, Vector3 rhs)
	{
		return lhs.x * rhs.x + lhs.y * rhs.y + lhs.z * rhs.z;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static Vector3 operator +(Vector3 a, Vector3 b)
	{
		return new Vector3
		{
			x = a.x + b.x,
			y = a.y + b.y,
			z = a.z + b.z
		};
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static Vector3 operator -(Vector3 a, Vector3 b)
	{
		return new Vector3
		{
			x = a.x - b.x,
			y = a.y - b.y,
			z = a.z - b.z
		};
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static Vector3 operator -(Vector3 a)
	{
		return new Vector3
		{
			x = 0f - a.x,
			y = 0f - a.y,
			z = 0f - a.z
		};
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static Vector3 operator *(Vector3 a, float d)
	{
		return new Vector3
		{
			x = a.x * d,
			y = a.y * d,
			z = a.z * d
		};
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static Vector3 operator *(float d, Vector3 a)
	{
		return new Vector3
		{
			x = a.x * d,
			y = a.y * d,
			z = a.z * d
		};
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static Vector3 operator /(Vector3 a, float d)
	{
		return new Vector3
		{
			x = a.x / d,
			y = a.y / d,
			z = a.z / d
		};
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static bool operator ==(Vector3 lhs, Vector3 rhs)
	{
		float num = lhs.x - rhs.x;
		float num2 = lhs.y - rhs.y;
		float num3 = lhs.z - rhs.z;
		float num4 = num * num + num2 * num2 + num3 * num3;
		return num4 < 9.9999994E-11f;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static bool operator !=(Vector3 lhs, Vector3 rhs)
	{
		return !(lhs == rhs);
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public override readonly string ToString()
	{
		return ToString(null, null);
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public readonly string ToString(string format)
	{
		return ToString(format, null);
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public readonly string ToString(string format, IFormatProvider formatProvider)
	{
		if (string.IsNullOrEmpty(format))
		{
			format = "F2";
		}
		if (formatProvider == null)
		{
			formatProvider = CultureInfo.InvariantCulture.NumberFormat;
		}
		return $"({x.ToString(format, formatProvider)}, {y.ToString(format, formatProvider)}, {z.ToString(format, formatProvider)})";
	}
}
