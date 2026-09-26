using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace UnityEngine;

[StructLayout(LayoutKind.Explicit)]
public struct Color32 : IEquatable<Color32>, IFormattable
{
	[FieldOffset(0)]
	private int rgba;

	[FieldOffset(0)]
	public byte r;

	[FieldOffset(1)]
	public byte g;

	[FieldOffset(2)]
	public byte b;

	[FieldOffset(3)]
	public byte a;

	public byte this[int index]
	{
		readonly get
		{
			return index switch
			{
				0 => r,
				1 => g,
				2 => b,
				3 => a,
				_ => throw new IndexOutOfRangeException("Invalid Color32 index(" + index + ")!"),
			};
		}
		set
		{
			switch (index)
			{
			case 0:
				r = value;
				break;
			case 1:
				g = value;
				break;
			case 2:
				b = value;
				break;
			case 3:
				a = value;
				break;
			default:
				throw new IndexOutOfRangeException("Invalid Color32 index(" + index + ")!");
			}
		}
	}

	public Color32(byte r, byte g, byte b, byte a)
	{
		rgba = 0;
		this.r = r;
		this.g = g;
		this.b = b;
		this.a = a;
	}

	public static implicit operator Color32(Color c)
	{
		return new Color32
		{
			r = (byte)Mathf.Round(Mathf.Clamp01(c.r) * 255f),
			g = (byte)Mathf.Round(Mathf.Clamp01(c.g) * 255f),
			b = (byte)Mathf.Round(Mathf.Clamp01(c.b) * 255f),
			a = (byte)Mathf.Round(Mathf.Clamp01(c.a) * 255f)
		};
	}

	public static implicit operator Color(Color32 c)
	{
		return new Color
		{
			r = (float)(int)c.r / 255f,
			g = (float)(int)c.g / 255f,
			b = (float)(int)c.b / 255f,
			a = (float)(int)c.a / 255f
		};
	}

	public static Color32 Lerp(Color32 a, Color32 b, float t)
	{
		t = Mathf.Clamp01(t);
		return new Color32
		{
			r = (byte)((float)(int)a.r + (float)(b.r - a.r) * t),
			g = (byte)((float)(int)a.g + (float)(b.g - a.g) * t),
			b = (byte)((float)(int)a.b + (float)(b.b - a.b) * t),
			a = (byte)((float)(int)a.a + (float)(b.a - a.a) * t)
		};
	}

	public static Color32 Lerp(in Color32 a, in Color32 b, float t)
	{
		t = Mathf.Clamp01(t);
		return new Color32
		{
			r = (byte)((float)(int)a.r + (float)(b.r - a.r) * t),
			g = (byte)((float)(int)a.g + (float)(b.g - a.g) * t),
			b = (byte)((float)(int)a.b + (float)(b.b - a.b) * t),
			a = (byte)((float)(int)a.a + (float)(b.a - a.a) * t)
		};
	}

	public static Color32 LerpUnclamped(Color32 a, Color32 b, float t)
	{
		return new Color32
		{
			r = (byte)((float)(int)a.r + (float)(b.r - a.r) * t),
			g = (byte)((float)(int)a.g + (float)(b.g - a.g) * t),
			b = (byte)((float)(int)a.b + (float)(b.b - a.b) * t),
			a = (byte)((float)(int)a.a + (float)(b.a - a.a) * t)
		};
	}

	public static Color32 LerpUnclamped(in Color32 a, in Color32 b, float t)
	{
		return new Color32
		{
			r = (byte)((float)(int)a.r + (float)(b.r - a.r) * t),
			g = (byte)((float)(int)a.g + (float)(b.g - a.g) * t),
			b = (byte)((float)(int)a.b + (float)(b.b - a.b) * t),
			a = (byte)((float)(int)a.a + (float)(b.a - a.a) * t)
		};
	}

	public override readonly int GetHashCode()
	{
		return rgba.GetHashCode();
	}

	public override readonly bool Equals(object other)
	{
		if (other is Color32 other2)
		{
			return Equals(in other2);
		}
		return false;
	}

	public readonly bool Equals(Color32 other)
	{
		return rgba == other.rgba;
	}

	public readonly bool Equals(in Color32 other)
	{
		return rgba == other.rgba;
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
		if (formatProvider == null)
		{
			formatProvider = CultureInfo.InvariantCulture.NumberFormat;
		}
		return $"RGBA({r.ToString(format, formatProvider)}, {g.ToString(format, formatProvider)}, {b.ToString(format, formatProvider)}, {a.ToString(format, formatProvider)})";
	}
}
