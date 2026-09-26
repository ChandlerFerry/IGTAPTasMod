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

	private static readonly Rect kZero = new Rect(0f, 0f, 0f, 0f);

	public static Rect zero
	{
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		get
		{
			return kZero;
		}
	}

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

	public Vector2 position
	{
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		readonly get
		{
			return new Vector2
			{
				x = m_XMin,
				y = m_YMin
			};
		}
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		set
		{
			m_XMin = value.x;
			m_YMin = value.y;
		}
	}

	public Vector2 center
	{
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		readonly get
		{
			return new Vector2
			{
				x = m_XMin + m_Width * 0.5f,
				y = m_YMin + m_Height * 0.5f
			};
		}
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		set
		{
			m_XMin = value.x - m_Width * 0.5f;
			m_YMin = value.y - m_Height * 0.5f;
		}
	}

	public Vector2 min
	{
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		readonly get
		{
			return new Vector2
			{
				x = m_XMin,
				y = m_YMin
			};
		}
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		set
		{
			xMin = value.x;
			yMin = value.y;
		}
	}

	public Vector2 max
	{
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		readonly get
		{
			return new Vector2
			{
				x = xMax,
				y = yMax
			};
		}
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		set
		{
			xMax = value.x;
			yMax = value.y;
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

	public readonly float left
	{
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		get
		{
			return m_XMin;
		}
	}

	public readonly float right
	{
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		get
		{
			return m_XMin + m_Width;
		}
	}

	public readonly float top
	{
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		get
		{
			return m_YMin;
		}
	}

	public readonly float bottom
	{
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		get
		{
			return m_YMin + m_Height;
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
	public Rect(Vector2 position, Vector2 size)
	{
		m_XMin = position.x;
		m_YMin = position.y;
		m_Width = size.x;
		m_Height = size.y;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public Rect(in Vector2 position, in Vector2 size)
	{
		m_XMin = position.x;
		m_YMin = position.y;
		m_Width = size.x;
		m_Height = size.y;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public Rect(Rect source)
	{
		m_XMin = source.m_XMin;
		m_YMin = source.m_YMin;
		m_Width = source.m_Width;
		m_Height = source.m_Height;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public Rect(in Rect source)
	{
		m_XMin = source.m_XMin;
		m_YMin = source.m_YMin;
		m_Width = source.m_Width;
		m_Height = source.m_Height;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static Rect MinMaxRect(float xmin, float ymin, float xmax, float ymax)
	{
		return new Rect
		{
			m_XMin = xmin,
			m_YMin = ymin,
			m_Width = xmax - xmin,
			m_Height = ymax - ymin
		};
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public void Set(float x, float y, float width, float height)
	{
		m_XMin = x;
		m_YMin = y;
		m_Width = width;
		m_Height = height;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public readonly bool Contains(Vector2 point)
	{
		return point.x >= m_XMin && point.x < xMax && point.y >= m_YMin && point.y < yMax;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public readonly bool Contains(in Vector2 point)
	{
		return point.x >= m_XMin && point.x < xMax && point.y >= m_YMin && point.y < yMax;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public readonly bool Contains(Vector3 point)
	{
		return point.x >= m_XMin && point.x < xMax && point.y >= m_YMin && point.y < yMax;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public readonly bool Contains(in Vector3 point)
	{
		return point.x >= m_XMin && point.x < xMax && point.y >= m_YMin && point.y < yMax;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public readonly bool Contains(Vector3 point, bool allowInverse)
	{
		if (!allowInverse)
		{
			return Contains(in point);
		}
		float num = xMax;
		float num2 = yMax;
		bool flag = (m_Width < 0f && point.x <= m_XMin && point.x > num) || (m_Width >= 0f && point.x >= m_XMin && point.x < num);
		bool flag2 = (m_Height < 0f && point.y <= m_YMin && point.y > num2) || (m_Height >= 0f && point.y >= m_YMin && point.y < num2);
		return flag && flag2;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public readonly bool Contains(in Vector3 point, bool allowInverse)
	{
		if (!allowInverse)
		{
			return Contains(in point);
		}
		float num = xMax;
		float num2 = yMax;
		bool flag = (m_Width < 0f && point.x <= m_XMin && point.x > num) || (m_Width >= 0f && point.x >= m_XMin && point.x < num);
		bool flag2 = (m_Height < 0f && point.y <= m_YMin && point.y > num2) || (m_Height >= 0f && point.y >= m_YMin && point.y < num2);
		return flag && flag2;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static Rect OrderMinMax(Rect rect)
	{
		float b = rect.xMax;
		float b2 = rect.yMax;
		return MinMaxRect(Mathf.Min(rect.m_XMin, b), Mathf.Min(rect.m_YMin, b2), Mathf.Max(rect.m_XMin, b), Mathf.Max(rect.m_YMin, b2));
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static Rect OrderMinMax(in Rect rect)
	{
		float b = rect.xMax;
		float b2 = rect.yMax;
		return MinMaxRect(Mathf.Min(rect.m_XMin, b), Mathf.Min(rect.m_YMin, b2), Mathf.Max(rect.m_XMin, b), Mathf.Max(rect.m_YMin, b2));
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public readonly bool Overlaps(Rect other)
	{
		return other.xMax > m_XMin && other.m_XMin < xMax && other.yMax > m_YMin && other.m_YMin < yMax;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public readonly bool Overlaps(in Rect other)
	{
		return other.xMax > m_XMin && other.m_XMin < xMax && other.yMax > m_YMin && other.m_YMin < yMax;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public readonly bool Overlaps(Rect other, bool allowInverse)
	{
		if (allowInverse)
		{
			other = OrderMinMax(in other);
			return OrderMinMax(in this).Overlaps(in other);
		}
		return Overlaps(in other);
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public readonly bool Overlaps(in Rect other, bool allowInverse)
	{
		if (allowInverse)
		{
			Rect other2 = OrderMinMax(in other);
			return OrderMinMax(in this).Overlaps(in other2);
		}
		return Overlaps(in other);
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static Vector2 NormalizedToPoint(Rect rectangle, Vector2 normalizedRectCoordinates)
	{
		return new Vector2
		{
			x = Mathf.Lerp(rectangle.m_XMin, rectangle.xMax, normalizedRectCoordinates.x),
			y = Mathf.Lerp(rectangle.m_YMin, rectangle.yMax, normalizedRectCoordinates.y)
		};
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static Vector2 NormalizedToPoint(in Rect rectangle, in Vector2 normalizedRectCoordinates)
	{
		return new Vector2
		{
			x = Mathf.Lerp(rectangle.m_XMin, rectangle.xMax, normalizedRectCoordinates.x),
			y = Mathf.Lerp(rectangle.m_YMin, rectangle.yMax, normalizedRectCoordinates.y)
		};
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static Vector2 PointToNormalized(Rect rectangle, Vector2 point)
	{
		return new Vector2
		{
			x = Mathf.InverseLerp(rectangle.m_XMin, rectangle.xMax, point.x),
			y = Mathf.InverseLerp(rectangle.m_YMin, rectangle.yMax, point.y)
		};
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static Vector2 PointToNormalized(in Rect rectangle, in Vector2 point)
	{
		return new Vector2
		{
			x = Mathf.InverseLerp(rectangle.m_XMin, rectangle.xMax, point.x),
			y = Mathf.InverseLerp(rectangle.m_YMin, rectangle.yMax, point.y)
		};
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
			return Equals(in other2);
		}
		return false;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public readonly bool Equals(Rect other)
	{
		return m_XMin.Equals(other.m_XMin) && m_YMin.Equals(other.m_YMin) && m_Width.Equals(other.m_Width) && m_Height.Equals(other.m_Height);
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public readonly bool Equals(in Rect other)
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
