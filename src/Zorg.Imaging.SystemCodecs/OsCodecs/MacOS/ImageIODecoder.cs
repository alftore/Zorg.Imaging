using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Zorg.Imaging.OsCodecs.Heif;

namespace Zorg.Imaging.OsCodecs.MacOS;

/// <summary>
/// Decodes HEIF files with Apple's ImageIO: HEIC from macOS 10.13, AVIF from macOS 13. The pixels are drawn into a
/// bitmap in the image's own colour space, so the colour isn't converted, and the orientation is left to Zorg.Imaging.
/// </summary>
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("maccatalyst")]
internal sealed unsafe partial class ImageIODecoder : SystemDecoder
{
    private const int ColorSpaceModelRgb = 1;
    private const uint AlphaPremultipliedLast = 1;
    private const uint ByteOrder16Little = 1 << 12;
    private const uint ByteOrder32Big = 4 << 12;

    private readonly Lazy<string[]> _types = new(SupportedTypes);

    public override string Name => "ImageIO";

    public override CodecAvailability Availability(ImageFormat format)
    {
        string type = format == ImageFormat.Avif ? "public.avif" : "public.heic";
        return Array.IndexOf(_types.Value, type) >= 0
            ? CodecAvailability.Available
            : CodecAvailability.Unavailable(format == ImageFormat.Avif ? "AVIF decoding through ImageIO needs macOS 13 or later." : "This system's ImageIO can't decode HEIC.");
    }

    public override Image Decode(ReadOnlyMemory<byte> data, HeifHeader header, CodecContext context)
    {
        PixelFormat target = Target(context);
        bool sixteen = target == PixelFormat.Rgba64;
        void* cfData = null;
        void* source = null;
        void* cgImage = null;
        void* space = null;
        void* bitmap = null;
        Image? image = null;
        try
        {
            using (System.Buffers.MemoryHandle pinned = data.Pin())
            {
                cfData = Native.CFDataCreate(null, (byte*)pinned.Pointer, data.Length);
            }

            if (cfData is null)
            {
                throw Failed("copying the file", header.Format);
            }

            void* noCache = Native.NoCacheOptions();
            source = Native.CGImageSourceCreateWithData(cfData, noCache);
            if (source is null || Native.CGImageSourceGetCount(source) < 1)
            {
                throw new ImageFormatException("ImageIO can't open the " + header.Format.Name + " file.");
            }

            cgImage = Native.CGImageSourceCreateImageAtIndex(source, 0, noCache);
            if (cgImage is null)
            {
                throw new ImageFormatException("ImageIO can't decode the " + header.Format.Name + " file's primary image.");
            }

            nint width = Native.CGImageGetWidth(cgImage);
            nint height = Native.CGImageGetHeight(cgImage);
            bool shown = width == header.DisplayWidth && height == header.DisplayHeight;
            bool cropped = width == header.Crop.Width && height == header.Crop.Height;
            if (!shown && !cropped && (width != header.Width || height != header.Height))
            {
                throw new ImageFormatException(string.Create(
                    CultureInfo.InvariantCulture,
                    $"The {header.Format.Name} file's image is {width}x{height} pixels, but its boxes say {header.Width}x{header.Height}, shown as {header.DisplayWidth}x{header.DisplayHeight}."));
            }

            // ImageIO reports the turn and mirror it left undone as an EXIF orientation; 1 means none left.
            bool applied = Native.OrientationOf(source) == 1;

            context.CancellationToken.ThrowIfCancellationRequested();

            // Keep the image's own RGB colour space, so nothing is converted; anything else is drawn as sRGB.
            void* own = Native.CGImageGetColorSpace(cgImage);
            bool keep = own is not null && Native.CGColorSpaceGetModel(own) == ColorSpaceModelRgb;
            space = keep ? Native.CGColorSpaceRetain(own) : Native.CGColorSpaceCreateWithName(Native.Constant("CoreGraphics", "kCGColorSpaceSRGB"));

            image = new Image((int)width, (int)height, sixteen ? PixelFormat.Rgba64 : PixelFormat.Rgba32);
            int bytesPerRow = (int)width * (sixteen ? 8 : 4);
            Span<byte> pixels = image.GetPixelBytes();
            fixed (byte* destination = pixels)
            {
                uint info = AlphaPremultipliedLast | (sixteen ? ByteOrder16Little : ByteOrder32Big);
                bitmap = Native.CGBitmapContextCreate(destination, width, height, sixteen ? 16 : 8, bytesPerRow, space, info);
                if (bitmap is null)
                {
                    throw Failed("making a bitmap", header.Format);
                }

                Native.CGContextDrawImage(bitmap, new Rect(0, 0, width, height), cgImage);
            }

            if (sixteen)
            {
                Unpremultiply16(pixels);
            }
            else
            {
                Unpremultiply8(pixels, swapRedBlue: target == PixelFormat.Bgra32);
            }

            if (target == PixelFormat.Bgra32)
            {
                image = Relabel(image, PixelFormat.Bgra32);
            }

            image.ColorSpace = keep ? header.ColorSpace ?? IccOf(space) ?? ColorSpace.Srgb : ColorSpace.Srgb;
            Image result = image;
            image = null;
            return AsShown(result, header, applied);
        }
        finally
        {
            image?.Dispose();
            Native.Release(bitmap);
            Native.Release(space);
            Native.Release(cgImage);
            Native.Release(source);
            Native.Release(cfData);
        }
    }

    // The bytes are already in BGRA order; copy them into an image that says so.
    private static Image Relabel(Image rgba, PixelFormat format)
    {
        var relabelled = new Image(rgba.Width, rgba.Height, format);
        rgba.GetPixelBytes().CopyTo(relabelled.GetPixelBytes());
        rgba.Dispose();
        return relabelled;
    }

    private static ColorSpace? IccOf(void* space)
    {
        void* icc = Native.CGColorSpaceCopyICCData(space);
        if (icc is null)
        {
            return null;
        }

        try
        {
            var profile = new ReadOnlySpan<byte>(Native.CFDataGetBytePtr(icc), (int)Native.CFDataGetLength(icc));
            return ColorSpace.FromIccProfile(profile);
        }
        catch (Exception e) when (e is ArgumentException or ImageException)
        {
            return null;
        }
        finally
        {
            Native.Release(icc);
        }
    }

    private static void Unpremultiply8(Span<byte> pixels, bool swapRedBlue)
    {
        for (int i = 0; i + 3 < pixels.Length; i += 4)
        {
            int a = pixels[i + 3];
            byte r = pixels[i];
            byte g = pixels[i + 1];
            byte b = pixels[i + 2];
            if (a is not (0 or 255))
            {
                r = (byte)Math.Min(255, ((r * 255) + (a / 2)) / a);
                g = (byte)Math.Min(255, ((g * 255) + (a / 2)) / a);
                b = (byte)Math.Min(255, ((b * 255) + (a / 2)) / a);
            }

            pixels[i] = swapRedBlue ? b : r;
            pixels[i + 1] = g;
            pixels[i + 2] = swapRedBlue ? r : b;
        }
    }

    private static void Unpremultiply16(Span<byte> pixels)
    {
        Span<ushort> values = MemoryMarshal.Cast<byte, ushort>(pixels);
        for (int i = 0; i + 3 < values.Length; i += 4)
        {
            int a = values[i + 3];
            if (a is 0 or 65535)
            {
                continue;
            }

            for (int c = 0; c < 3; c++)
            {
                values[i + c] = (ushort)Math.Min(65535L, ((values[i + c] * 65535L) + (a / 2)) / a);
            }
        }
    }

    private static string[] SupportedTypes()
    {
        try
        {
            return Native.SupportedTypes();
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return [];
        }
    }

    private static ImageCodecException Failed(string what, ImageFormat format) =>
        new(string.Create(CultureInfo.InvariantCulture, $"ImageIO failed {what} for a {format.Name} file."), "ImageIO", innerException: null);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct Rect(double x, double y, double width, double height)
    {
        public readonly double X = x;
        public readonly double Y = y;
        public readonly double Width = width;
        public readonly double Height = height;
    }

    /// <summary>The CoreFoundation, CoreGraphics and ImageIO functions used, all plain C.</summary>
    private static partial class Native
    {
        private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
        private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
        private const string ImageIO = "/System/Library/Frameworks/ImageIO.framework/ImageIO";
        private const int StringEncodingUtf8 = 0x08000100;
        private const int NumberIntType = 9;

        private static readonly Lazy<nint> NoCache = new(MakeNoCacheOptions);

        public static void* NoCacheOptions() => (void*)NoCache.Value;

        /// <summary>A global CFStringRef (or other object) a framework exports.</summary>
        public static void* Constant(string framework, string name)
        {
            nint library = NativeLibrary.Load(framework == "CoreGraphics" ? CoreGraphics : framework == "ImageIO" ? ImageIO : CoreFoundation);
            return *(void**)NativeLibrary.GetExport(library, name);
        }

        public static string[] SupportedTypes()
        {
            void* array = CGImageSourceCopyTypeIdentifiers();
            if (array is null)
            {
                return [];
            }

            try
            {
                nint count = CFArrayGetCount(array);
                var types = new string[count];
                byte* buffer = stackalloc byte[256];
                for (nint i = 0; i < count; i++)
                {
                    void* text = CFArrayGetValueAtIndex(array, i);
                    types[i] = CFStringGetCString(text, buffer, 256, StringEncodingUtf8)
                        ? Marshal.PtrToStringUTF8((nint)buffer) ?? string.Empty
                        : string.Empty;
                }

                return types;
            }
            finally
            {
                Release(array);
            }
        }

        /// <summary>The EXIF orientation ImageIO reports for the first image, 1 when it reports none.</summary>
        public static int OrientationOf(void* source)
        {
            void* properties = CGImageSourceCopyPropertiesAtIndex(source, 0, null);
            if (properties is null)
            {
                return 1;
            }

            try
            {
                void* number = CFDictionaryGetValue(properties, Constant("ImageIO", "kCGImagePropertyOrientation"));
                int value = 1;
                return number is not null && CFNumberGetValue(number, NumberIntType, &value) && value is >= 1 and <= 8 ? value : 1;
            }
            finally
            {
                Release(properties);
            }
        }

        public static void Release(void* instance)
        {
            if (instance is not null)
            {
                CFRelease(instance);
            }
        }

        private static nint MakeNoCacheOptions()
        {
            nint foundation = NativeLibrary.Load(CoreFoundation);
            void* key = Constant("ImageIO", "kCGImageSourceShouldCache");
            void* no = *(void**)NativeLibrary.GetExport(foundation, "kCFBooleanFalse");
            void* keyCallbacks = (void*)NativeLibrary.GetExport(foundation, "kCFTypeDictionaryKeyCallBacks");
            void* valueCallbacks = (void*)NativeLibrary.GetExport(foundation, "kCFTypeDictionaryValueCallBacks");
            return (nint)CFDictionaryCreate(null, &key, &no, 1, keyCallbacks, valueCallbacks);
        }

        [LibraryImport(CoreFoundation)]
        public static partial void* CFDataCreate(void* allocator, byte* bytes, nint length);

        [LibraryImport(CoreFoundation)]
        public static partial byte* CFDataGetBytePtr(void* data);

        [LibraryImport(CoreFoundation)]
        public static partial nint CFDataGetLength(void* data);

        [LibraryImport(CoreFoundation)]
        public static partial void CFRelease(void* instance);

        [LibraryImport(CoreFoundation)]
        public static partial void* CFDictionaryCreate(void* allocator, void** keys, void** values, nint count, void* keyCallbacks, void* valueCallbacks);

        [LibraryImport(CoreFoundation)]
        public static partial nint CFArrayGetCount(void* array);

        [LibraryImport(CoreFoundation)]
        public static partial void* CFArrayGetValueAtIndex(void* array, nint index);

        [LibraryImport(CoreFoundation)]
        [return: MarshalAs(UnmanagedType.U1)]
        public static partial bool CFStringGetCString(void* text, byte* buffer, nint size, int encoding);

        [LibraryImport(CoreFoundation)]
        public static partial void* CFDictionaryGetValue(void* dictionary, void* key);

        [LibraryImport(CoreFoundation)]
        [return: MarshalAs(UnmanagedType.U1)]
        public static partial bool CFNumberGetValue(void* number, int type, void* value);

        [LibraryImport(ImageIO)]
        public static partial void* CGImageSourceCopyPropertiesAtIndex(void* source, nint index, void* options);

        [LibraryImport(ImageIO)]
        public static partial void* CGImageSourceCopyTypeIdentifiers();

        [LibraryImport(ImageIO)]
        public static partial void* CGImageSourceCreateWithData(void* data, void* options);

        [LibraryImport(ImageIO)]
        public static partial nint CGImageSourceGetCount(void* source);

        [LibraryImport(ImageIO)]
        public static partial void* CGImageSourceCreateImageAtIndex(void* source, nint index, void* options);

        [LibraryImport(CoreGraphics)]
        public static partial nint CGImageGetWidth(void* image);

        [LibraryImport(CoreGraphics)]
        public static partial nint CGImageGetHeight(void* image);

        [LibraryImport(CoreGraphics)]
        public static partial void* CGImageGetColorSpace(void* image);

        [LibraryImport(CoreGraphics)]
        public static partial int CGColorSpaceGetModel(void* space);

        [LibraryImport(CoreGraphics)]
        public static partial void* CGColorSpaceRetain(void* space);

        [LibraryImport(CoreGraphics)]
        public static partial void* CGColorSpaceCreateWithName(void* name);

        [LibraryImport(CoreGraphics)]
        public static partial void* CGColorSpaceCopyICCData(void* space);

        [LibraryImport(CoreGraphics)]
        public static partial void* CGBitmapContextCreate(void* data, nint width, nint height, nint bitsPerComponent, nint bytesPerRow, void* space, uint bitmapInfo);

        [LibraryImport(CoreGraphics)]
        public static partial void CGContextDrawImage(void* context, Rect rect, void* image);
    }
}
