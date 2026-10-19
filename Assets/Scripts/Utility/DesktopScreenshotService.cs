using System;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;

namespace ScreamJam.Utility
{
    /// <summary>
    /// Captures the full Windows virtual desktop when possible, with a
    /// game-view fallback on other platforms or if GDI capture fails.
    /// </summary>
    public static class DesktopScreenshotService
    {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        private const int SmXVirtualScreen = 76;
        private const int SmYVirtualScreen = 77;
        private const int SmCxVirtualScreen = 78;
        private const int SmCyVirtualScreen = 79;
        private const int Srccopy = 0x00CC0020;
        private const int Captureblt = 0x40000000;
        private const uint DibRgbColors = 0;

        [StructLayout(LayoutKind.Sequential)]
        private struct BitmapInfoHeader
        {
            public uint biSize;
            public int biWidth;
            public int biHeight;
            public ushort biPlanes;
            public ushort biBitCount;
            public uint biCompression;
            public uint biSizeImage;
            public int biXPelsPerMeter;
            public int biYPelsPerMeter;
            public uint biClrUsed;
            public uint biClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BitmapInfo
        {
            public BitmapInfoHeader bmiHeader;
            public uint bmiColors;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int width, int height);

        [DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

        [DllImport("gdi32.dll")]
        private static extern bool BitBlt(
            IntPtr hdcDest,
            int xDest,
            int yDest,
            int width,
            int height,
            IntPtr hdcSrc,
            int xSrc,
            int ySrc,
            int rop);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern int GetDIBits(
            IntPtr hdc,
            IntPtr hbmp,
            uint uStartScan,
            uint cScanLines,
            byte[] lpvBits,
            ref BitmapInfo lpbi,
            uint uUsage);
#endif

        public static string ScreenshotDirectory
        {
            get { return Path.Combine(Application.persistentDataPath, "Screenshots"); }
        }

        public static string ScreenshotPath
        {
            get { return Path.Combine(ScreenshotDirectory, "last_screenshot.png"); }
        }

        public static bool TryCaptureAndSave(out bool fullDesktop, out string error)
        {
            Texture2D texture = null;

            try
            {
                texture = CaptureTexture(out fullDesktop, out error);
                if (texture == null)
                    return false;

                byte[] png = texture.EncodeToPNG();
                if (png == null || png.Length == 0)
                {
                    error = "Failed to encode screenshot.";
                    return false;
                }

                Directory.CreateDirectory(ScreenshotDirectory);
                File.WriteAllBytes(ScreenshotPath, png);
                error = null;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                fullDesktop = false;
                return false;
            }
            finally
            {
                if (texture != null)
                    UnityEngine.Object.Destroy(texture);
            }
        }

        public static Texture2D CaptureTexture(out bool fullDesktop, out string error)
        {
            string desktopError = null;

#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            Texture2D desktop = TryCaptureDesktop(out desktopError);
            if (desktop != null)
            {
                fullDesktop = true;
                error = null;
                return desktop;
            }
#endif

            try
            {
                Texture2D gameView = ScreenCapture.CaptureScreenshotAsTexture();
                if (gameView == null)
                {
                    error = string.IsNullOrEmpty(desktopError)
                        ? "ScreenCapture returned null."
                        : "Desktop capture failed: " + desktopError + " Fallback returned null.";
                    fullDesktop = false;
                    return null;
                }

                error = null;
                fullDesktop = false;
                return gameView;
            }
            catch (Exception ex)
            {
                error = string.IsNullOrEmpty(desktopError)
                    ? "Screenshot fallback failed: " + ex.Message
                    : "Desktop capture failed: " + desktopError + " Fallback failed: " + ex.Message;
                fullDesktop = false;
                return null;
            }
        }

#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        private static Texture2D TryCaptureDesktop(out string error)
        {
            error = null;

            IntPtr screenDc = IntPtr.Zero;
            IntPtr memoryDc = IntPtr.Zero;
            IntPtr bitmap = IntPtr.Zero;
            IntPtr previousObject = IntPtr.Zero;

            try
            {
                int left = GetSystemMetrics(SmXVirtualScreen);
                int top = GetSystemMetrics(SmYVirtualScreen);
                int width = GetSystemMetrics(SmCxVirtualScreen);
                int height = GetSystemMetrics(SmCyVirtualScreen);

                if (width <= 0 || height <= 0)
                {
                    error = "Invalid virtual screen size.";
                    return null;
                }

                screenDc = GetDC(IntPtr.Zero);
                if (screenDc == IntPtr.Zero)
                {
                    error = "Failed to get the screen device context.";
                    return null;
                }

                memoryDc = CreateCompatibleDC(screenDc);
                if (memoryDc == IntPtr.Zero)
                {
                    error = "Failed to create a memory device context.";
                    return null;
                }

                bitmap = CreateCompatibleBitmap(screenDc, width, height);
                if (bitmap == IntPtr.Zero)
                {
                    error = "Failed to create the screen bitmap.";
                    return null;
                }

                previousObject = SelectObject(memoryDc, bitmap);
                if (!BitBlt(
                        memoryDc,
                        0,
                        0,
                        width,
                        height,
                        screenDc,
                        left,
                        top,
                        Srccopy | Captureblt))
                {
                    error = "BitBlt failed.";
                    return null;
                }

                var info = new BitmapInfo
                {
                    bmiHeader = new BitmapInfoHeader
                    {
                        biSize = (uint)Marshal.SizeOf(typeof(BitmapInfoHeader)),
                        biWidth = width,
                        biHeight = height,
                        biPlanes = 1,
                        biBitCount = 32,
                        biCompression = 0,
                        biSizeImage = (uint)(width * height * 4)
                    }
                };

                byte[] bgra = new byte[width * height * 4];
                int lines = GetDIBits(
                    memoryDc,
                    bitmap,
                    0,
                    (uint)height,
                    bgra,
                    ref info,
                    DibRgbColors);

                if (lines == 0)
                {
                    error = "GetDIBits failed.";
                    return null;
                }

                var pixels = new Color32[width * height];

                // GetDIBits with a positive biHeight returns bottom-up rows,
                // which is also the order SetPixels32 expects.
                for (int i = 0; i < pixels.Length; i++)
                {
                    int sourcePixel = i * 4;
                    byte b = bgra[sourcePixel];
                    byte g = bgra[sourcePixel + 1];
                    byte r = bgra[sourcePixel + 2];
                    pixels[i] = new Color32(r, g, b, 255);
                }

                var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
                texture.SetPixels32(pixels);
                texture.Apply(false, false);
                return texture;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return null;
            }
            finally
            {
                if (memoryDc != IntPtr.Zero)
                {
                    if (previousObject != IntPtr.Zero)
                        SelectObject(memoryDc, previousObject);

                    DeleteDC(memoryDc);
                }

                if (bitmap != IntPtr.Zero)
                    DeleteObject(bitmap);

                if (screenDc != IntPtr.Zero)
                    ReleaseDC(IntPtr.Zero, screenDc);
            }
        }
#endif
    }
}
