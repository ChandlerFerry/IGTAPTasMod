using System;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace UnityEngine;

public struct Color : IEquatable<Color>, IFormattable
{
	public float r;

	public float g;

	public float b;

	public float a;

	public static Color black
	{
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		get
		{
			return new Color(0f, 0f, 0f, 1f);
		}
	}

	public static Color blue
	{
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		get
		{
			return new Color(0f, 0f, 1f, 1f);
		}
	}

	public static Color green
	{
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		get
		{
			return new Color(0f, 1f, 0f, 1f);
		}
	}

	public static Color red
	{
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		get
		{
			return new Color(1f, 0f, 0f, 1f);
		}
	}

	public static Color white
	{
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		get
		{
			return new Color(1f, 1f, 1f, 1f);
		}
	}

	public Color(float r, float g, float b, float a)
	{
		this.r = r;
		this.g = g;
		this.b = b;
		this.a = a;
	}

	public Color(float r, float g, float b)
	{
		this.r = r;
		this.g = g;
		this.b = b;
		a = 1f;
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
			format = "F3";
		}
		if (formatProvider == null)
		{
			formatProvider = CultureInfo.InvariantCulture.NumberFormat;
		}
		return $"RGBA({r.ToString(format, formatProvider)}, {g.ToString(format, formatProvider)}, {b.ToString(format, formatProvider)}, {a.ToString(format, formatProvider)})";
	}

	public override readonly int GetHashCode()
	{
		return r.GetHashCode() ^ (g.GetHashCode() << 2) ^ (b.GetHashCode() >> 2) ^ (a.GetHashCode() >> 1);
	}

	public override readonly bool Equals(object other)
	{
		if (other is Color other2)
		{
			return Equals(other2);
		}
		return false;
	}

	public readonly bool Equals(Color other)
	{
		return r.Equals(other.r) && g.Equals(other.g) && b.Equals(other.b) && a.Equals(other.a);
	}

	public static Color operator +(Color a, Color b)
	{
		return new Color
		{
			r = a.r + b.r,
			g = a.g + b.g,
			b = a.b + b.b,
			a = a.a + b.a
		};
	}

	public static Color operator -(Color a, Color b)
	{
		return new Color
		{
			r = a.r - b.r,
			g = a.g - b.g,
			b = a.b - b.b,
			a = a.a - b.a
		};
	}

	public static Color operator *(Color a, float b)
	{
		return new Color
		{
			r = a.r * b,
			g = a.g * b,
			b = a.b * b,
			a = a.a * b
		};
	}

	public static Color operator /(Color a, float b)
	{
		return new Color
		{
			r = a.r / b,
			g = a.g / b,
			b = a.b / b,
			a = a.a / b
		};
	}

	public static bool operator ==(Color lhs, Color rhs)
	{
		float num = lhs.r - rhs.r;
		float num2 = lhs.g - rhs.g;
		float num3 = lhs.b - rhs.b;
		float num4 = lhs.a - rhs.a;
		float num5 = num * num + num2 * num2 + num3 * num3 + num4 * num4;
		return num5 < 9.9999994E-11f;
	}

	public static bool operator !=(Color lhs, Color rhs)
	{
		return !(lhs == rhs);
	}
}
