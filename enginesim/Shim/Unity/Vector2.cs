using System;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace UnityEngine;

public struct Vector2 : IEquatable<Vector2>, IFormattable
{
	public float x;

	public float y;

	private static readonly Vector2 zeroVector = new Vector2(0f, 0f);

	private static readonly Vector2 oneVector = new Vector2(1f, 1f);

	private static readonly Vector2 upVector = new Vector2(0f, 1f);

	private static readonly Vector2 downVector = new Vector2(0f, -1f);

	private static readonly Vector2 leftVector = new Vector2(-1f, 0f);

	private static readonly Vector2 rightVector = new Vector2(1f, 0f);

	public readonly Vector2 normalized
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
			return (float)Math.Sqrt(x * x + y * y);
		}
	}

	public readonly float sqrMagnitude
	{
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		get
		{
			return x * x + y * y;
		}
	}

	public static Vector2 zero
	{
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		get
		{
			return zeroVector;
		}
	}

	public static Vector2 one
	{
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		get
		{
			return oneVector;
		}
	}

	public static Vector2 up
	{
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		get
		{
			return upVector;
		}
	}

	public static Vector2 down
	{
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		get
		{
			return downVector;
		}
	}

	public static Vector2 left
	{
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		get
		{
			return leftVector;
		}
	}

	public static Vector2 right
	{
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		get
		{
			return rightVector;
		}
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public Vector2(float x, float y)
	{
		this.x = x;
		this.y = y;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static Vector2 Lerp(Vector2 a, Vector2 b, float t)
	{
		t = Mathf.Clamp01(t);
		Vector2 result = default(Vector2);
		result.x = a.x + (b.x - a.x) * t;
		result.y = a.y + (b.y - a.y) * t;
		return result;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static Vector2 MoveTowards(Vector2 current, Vector2 target, float maxDistanceDelta)
	{
		float num = target.x - current.x;
		float num2 = target.y - current.y;
		float num3 = num * num + num2 * num2;
		if (num3 == 0f || (maxDistanceDelta >= 0f && num3 <= maxDistanceDelta * maxDistanceDelta))
		{
			return target;
		}
		float num4 = (float)Math.Sqrt(num3);
		Vector2 result = default(Vector2);
		result.x = current.x + num / num4 * maxDistanceDelta;
		result.y = current.y + num2 / num4 * maxDistanceDelta;
		return result;
	}

	public static Vector2 Normalize(Vector2 value)
	{
		float num = value.magnitude;
		return (num > 1E-05f) ? new Vector2
		{
			x = value.x / num,
			y = value.y / num
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
		}
		else
		{
			x = 0f;
			y = 0f;
		}
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
		return $"({x.ToString(format, formatProvider)}, {y.ToString(format, formatProvider)})";
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public override readonly int GetHashCode()
	{
		return x.GetHashCode() ^ (y.GetHashCode() << 2);
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public override readonly bool Equals(object other)
	{
		if (other is Vector2 other2)
		{
			return Equals(other2);
		}
		return false;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public readonly bool Equals(Vector2 other)
	{
		return x == other.x && y == other.y;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static float Dot(Vector2 lhs, Vector2 rhs)
	{
		return lhs.x * rhs.x + lhs.y * rhs.y;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static float Distance(Vector2 a, Vector2 b)
	{
		float num = a.x - b.x;
		float num2 = a.y - b.y;
		return (float)Math.Sqrt(num * num + num2 * num2);
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static Vector2 operator +(Vector2 a, Vector2 b)
	{
		return new Vector2
		{
			x = a.x + b.x,
			y = a.y + b.y
		};
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static Vector2 operator -(Vector2 a, Vector2 b)
	{
		return new Vector2
		{
			x = a.x - b.x,
			y = a.y - b.y
		};
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static Vector2 operator *(Vector2 a, Vector2 b)
	{
		return new Vector2
		{
			x = a.x * b.x,
			y = a.y * b.y
		};
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static Vector2 operator -(Vector2 a)
	{
		return new Vector2
		{
			x = 0f - a.x,
			y = 0f - a.y
		};
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static Vector2 operator *(Vector2 a, float d)
	{
		return new Vector2
		{
			x = a.x * d,
			y = a.y * d
		};
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static Vector2 operator *(float d, Vector2 a)
	{
		return new Vector2
		{
			x = a.x * d,
			y = a.y * d
		};
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static Vector2 operator /(Vector2 a, float d)
	{
		return new Vector2
		{
			x = a.x / d,
			y = a.y / d
		};
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static bool operator ==(Vector2 lhs, Vector2 rhs)
	{
		float num = lhs.x - rhs.x;
		float num2 = lhs.y - rhs.y;
		return num * num + num2 * num2 < 9.9999994E-11f;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static bool operator !=(Vector2 lhs, Vector2 rhs)
	{
		return !(lhs == rhs);
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static implicit operator Vector2(Vector3 v)
	{
		return new Vector2
		{
			x = v.x,
			y = v.y
		};
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static implicit operator Vector3(Vector2 v)
	{
		return new Vector3
		{
			x = v.x,
			y = v.y,
			z = 0f
		};
	}
}
