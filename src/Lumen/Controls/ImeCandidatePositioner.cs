using System.Runtime.InteropServices;
using System.Windows;

namespace Lumen.Controls;

/// <summary>
/// Positions the IME candidate window near the terminal caret. WPF only does
/// this automatically for its built-in text editors; a custom FrameworkElement
/// needs to set the candidate position through IMM32 itself.
/// </summary>
internal static class ImeCandidatePositioner
{
    private const int CfsCandidatePos = 0x0040;

    public static void Set(IntPtr windowHandle, Point screenPoint)
    {
        if (windowHandle == IntPtr.Zero) return;
        IntPtr context = ImmGetContext(windowHandle);
        if (context == IntPtr.Zero) return;

        try
        {
            var form = new CandidateForm
            {
                dwIndex = 0,
                dwStyle = CfsCandidatePos,
                ptCurrentPos = new NativePoint
                {
                    X = (int)Math.Round(screenPoint.X),
                    Y = (int)Math.Round(screenPoint.Y)
                }
            };
            ImmSetCandidateWindow(context, ref form);
        }
        finally
        {
            ImmReleaseContext(windowHandle, context);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CandidateForm
    {
        public int dwIndex;
        public int dwStyle;
        public NativePoint ptCurrentPos;
        public NativeRect rcArea;
    }

    [DllImport("imm32.dll")]
    private static extern IntPtr ImmGetContext(IntPtr hWnd);

    [DllImport("imm32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ImmSetCandidateWindow(IntPtr hIMC, ref CandidateForm form);

    [DllImport("imm32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ImmReleaseContext(IntPtr hWnd, IntPtr hIMC);
}
