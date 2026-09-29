using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace UnityEngine;

[StructLayout(LayoutKind.Sequential, Size = 1)]
public struct Mathf
{
	public static float Sin(float f)
	{
		return (float)Math.Sin(f);
	}

	public static float Cos(float f)
	{
		return (float)Math.Cos(f);
	}

	public static float Abs(float f)
	{
		return Math.Abs(f);
	}

	public static float Min(float a, float b)
	{
		return (a < b) ? a : b;
	}

	public static int Min(int a, int b)
	{
		return (a < b) ? a : b;
	}

	public static float Max(float a, float b)
	{
		return (a > b) ? a : b;
	}

	public static int Max(int a, int b)
	{
		return (a > b) ? a : b;
	}

	public static float Pow(float f, float p)
	{
		return (float)Math.Pow(f, p);
	}

	public static float Log10(float f)
	{
		return (float)Math.Log10(f);
	}

	public static float Round(float f)
	{
		return (float)Math.Round(f);
	}

	public static int CeilToInt(float f)
	{
		return (int)Math.Ceiling(f);
	}

	public static int FloorToInt(float f)
	{
		return (int)Math.Floor(f);
	}

	public static int RoundToInt(float f)
	{
		return (int)Math.Round(f);
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static float Sign(float f)
	{
		return (f >= 0f) ? 1f : (-1f);
	}

	public static float Clamp(float value, float min, float max)
	{
		if (value < min)
		{
			value = min;
		}
		else if (value > max)
		{
			value = max;
		}
		return value;
	}

	public static int Clamp(int value, int min, int max)
	{
		if (value < min)
		{
			value = min;
		}
		else if (value > max)
		{
			value = max;
		}
		return value;
	}

	public static float Clamp01(float value)
	{
		if (value < 0f)
		{
			return 0f;
		}
		if (value > 1f)
		{
			return 1f;
		}
		return value;
	}

	public static float Lerp(float a, float b, float t)
	{
		return a + (b - a) * Clamp01(t);
	}
}
