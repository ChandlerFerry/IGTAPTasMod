using System;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace UnityEngine;

public struct Rect : IEquatable<Rect>, IFormattable
{
	private float m_XMin;

	private float m_YMin;

	private float m_Width;

	private float m_Height;

	public float x
	{
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		readonly get
		{
			return m_XMin;
		}
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		set
		{
			m_XMin = value;
		}
	}

	public float y
	{
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		readonly get
		{
			return m_YMin;
		}
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		set
		{
			m_YMin = value;
		}
	}

	public float width
	{
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		readonly get
		{
			return m_Width;
		}
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		set
		{
			m_Width = value;
		}
	}

	public float height
	{
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		readonly get
		{
			return m_Height;
		}
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		set
		{
			m_Height = value;
		}
	}

	public Vector2 size
	{
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		readonly get
		{
			return new Vector2
			{
				x = m_Width,
				y = m_Height
			};
		}
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		set
		{
			m_Width = value.x;
			m_Height = value.y;
		}
	}

	public float xMin
	{
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		readonly get
		{
			return m_XMin;
		}
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		set
		{
			float num = xMax;
			m_XMin = value;
			m_Width = num - m_XMin;
		}
	}

	public float yMin
	{
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		readonly get
		{
			return m_YMin;
		}
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		set
		{
			float num = yMax;
			m_YMin = value;
			m_Height = num - m_YMin;
		}
	}

	public float xMax
	{
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		readonly get
		{
			return m_Width + m_XMin;
		}
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		set
		{
			m_Width = value - m_XMin;
		}
	}

	public float yMax
	{
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		readonly get
		{
			return m_Height + m_YMin;
		}
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		set
		{
			m_Height = value - m_YMin;
		}
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public Rect(float x, float y, float width, float height)
	{
		m_XMin = x;
		m_YMin = y;
		m_Width = width;
		m_Height = height;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static bool operator !=(Rect lhs, Rect rhs)
	{
		return !(lhs == rhs);
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static bool operator ==(Rect lhs, Rect rhs)
	{
		return lhs.m_XMin == rhs.m_XMin && lhs.m_YMin == rhs.m_YMin && lhs.m_Width == rhs.m_Width && lhs.m_Height == rhs.m_Height;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public override readonly int GetHashCode()
	{
		return m_XMin.GetHashCode() ^ (m_Width.GetHashCode() << 2) ^ (m_YMin.GetHashCode() >> 2) ^ (m_Height.GetHashCode() >> 1);
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public override readonly bool Equals(object other)
	{
		if (other is Rect other2)
		{
			return Equals(other2);
		}
		return false;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public readonly bool Equals(Rect other)
	{
		return m_XMin.Equals(other.m_XMin) && m_YMin.Equals(other.m_YMin) && m_Width.Equals(other.m_Width) && m_Height.Equals(other.m_Height);
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
		return $"(x:{m_XMin.ToString(format, formatProvider)}, y:{m_YMin.ToString(format, formatProvider)}, width:{m_Width.ToString(format, formatProvider)}, height:{m_Height.ToString(format, formatProvider)})";
	}
}
