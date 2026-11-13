using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Zorg.Imaging.OsCodecs.Heif;

namespace Zorg.Imaging.OsCodecs.Windows;

/// <summary>
/// Decodes HEIF files with the Windows Imaging Component's HEIF decoder, which comes with the Microsoft Store's
/// HEIF Image Extension and needs HEVC Video Extensions (HEIC) or the AV1 Video Extension (AVIF) for the pixels.
/// </summary>
/// <remarks>
/// <para>
/// The decoder is created by its class ID, never by asking WIC which installed codec claims the file, so a third-party
/// WIC codec never sees it. COM is entered as a multithreaded apartment unless the thread already has one (a WPF or
/// WinForms UI thread is single-threaded, and WIC works there too). Methods are called through the COM vtables, which
/// keeps trimming and Native AOT working.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed unsafe class WicDecoder : SystemDecoder
{
    private const int ComMultithreaded = 0;
    private const int ClassContextInProcessServer = 1;
    private const int DecodeMetadataCacheOnDemand = 0;
    private const int DitherNone = 0;
    private const int PaletteCustom = 0;
    private const int InterpolationFant = 3;
    private const int SOk = 0;
    private const int SFalse = 1;
    private const int ChangedMode = unchecked((int)0x80010106);
    private const int ClassNotRegistered = unchecked((int)0x80040154);
    private const int ComponentNotFound = unchecked((int)0x88982F50);
    private const int BadImage = unchecked((int)0x88982F60);
    private const int BadHeader = unchecked((int)0x88982F61);
    private const int UnsupportedOperation = unchecked((int)0x88982F81);
    private const int UnsupportedPixelFormat = unchecked((int)0x88982F80);
    private const int CodecNotFound = unchecked((int)0xC00D5212); // MF_E_TOPO_CODEC_NOT_FOUND
    private const int InvalidFileFormat = unchecked((int)0xC00D36BE); // MF_E_INVALID_FILE_FORMAT

    // From wincodec.h (Windows SDK 10.0.26100).
    private static readonly Guid ImagingFactory = new(0xcacaf262, 0x9370, 0x4615, 0xa1, 0x3b, 0x9f, 0x55, 0x39, 0xda, 0x4c, 0x0a);
    private static readonly Guid ImagingFactoryInterface = new(0xec5ec8a9, 0xc395, 0x4314, 0x9c, 0x77, 0x54, 0xd7, 0xa9, 0x35, 0xff, 0x70);
    private static readonly Guid HeifDecoder = new(0xe9a4a80a, 0x44fe, 0x4de4, 0x89, 0x71, 0x71, 0x50, 0xb1, 0x0a, 0x51, 0x99);
    private static readonly Guid BitmapDecoderInterface = new(0x9edde9e7, 0x8dee, 0x47ea, 0x99, 0xdf, 0xe6, 0xfa, 0xf2, 0xed, 0x44, 0xbf);
    private static readonly Guid Rgba32 = new(0xf5c7ad2d, 0x6a8d, 0x43dd, 0xa7, 0xa8, 0xa2, 0x99, 0x35, 0x26, 0x1a, 0xe9);
    private static readonly Guid Bgra32 = new(0x6fddc324, 0x4e03, 0x4bfe, 0xb1, 0x85, 0x3d, 0x77, 0x76, 0x8d, 0xc9, 0x0f);
    private static readonly Guid Rgba64 = new(0x6fddc324, 0x4e03, 0x4bfe, 0xb1, 0x85, 0x3d, 0x77, 0x76, 0x8d, 0xc9, 0x16);
    private static readonly Guid Alpha8 = new(0xe6cd0116, 0xeeba, 0x4161, 0xaa, 0x85, 0x27, 0xdd, 0x9f, 0xb3, 0xa8, 0x95);
    private static readonly Guid SourceTransformInterface = new(0x3b16811b, 0x6a43, 0x4ec9, 0xb7, 0x13, 0x3d, 0x5a, 0x0c, 0x13, 0xb9, 0x40);

    private readonly Lazy<bool> _heifExtension = new(FindHeifExtension);

    public override string Name => "Windows Imaging Component";

    public override CodecAvailability Availability(ImageFormat format) =>
        _heifExtension.Value ? CodecAvailability.Available : CodecAvailability.Unavailable(Missing(format));

    public override Image Decode(ReadOnlyMemory<byte> data, HeifHeader header, CodecContext context, (int Width, int Height)? scaledTo)
    {
        PixelFormat target = Target(context);
        Guid wicFormat = target switch
        {
            PixelFormat.Rgba32 => Rgba32,
            PixelFormat.Bgra32 => Bgra32,
            _ => Rgba64,
        };

        using var com = new ComScope();
        void* factory = null;
        void* stream = null;
        void* decoder = null;
        void* frame = null;
        void* converter = null;
        void* scaler = null;
        Image? image = null;
        try
        {
            Check(CreateInstance(ImagingFactory, ImagingFactoryInterface, &factory), "creating the imaging factory", header.Format);
            Check(CreateHeifDecoder(&decoder), "creating the HEIF decoder", header.Format);

            using System.Buffers.MemoryHandle pinned = data.Pin();
            Check(Call(factory, 14, &stream), "creating a stream", header.Format); // IWICImagingFactory::CreateStream
            Check(((delegate* unmanaged[Stdcall]<void*, byte*, uint, int>)Vtable(stream)[16])(stream, (byte*)pinned.Pointer, (uint)data.Length), "reading the file", header.Format); // IWICStream::InitializeFromMemory
            Check(((delegate* unmanaged[Stdcall]<void*, void*, int, int>)Vtable(decoder)[4])(decoder, stream, DecodeMetadataCacheOnDemand), "opening the file", header.Format); // IWICBitmapDecoder::Initialize
            Check(((delegate* unmanaged[Stdcall]<void*, uint, void**, int>)Vtable(decoder)[13])(decoder, 0, &frame), "reading the primary image", header.Format); // IWICBitmapDecoder::GetFrame

            uint width;
            uint height;
            Check(((delegate* unmanaged[Stdcall]<void*, uint*, uint*, int>)Vtable(frame)[3])(frame, &width, &height), "reading the size", header.Format); // IWICBitmapSource::GetSize
            // WIC applies HEIF's crop, turn and mirror itself, so this is normally the size as shown.
            if ((width != (uint)header.DisplayWidth || height != (uint)header.DisplayHeight) && (width != (uint)header.Width || height != (uint)header.Height))
            {
                throw new ImageFormatException(string.Create(
                    CultureInfo.InvariantCulture,
                    $"The {header.Format.Name} file's image is {width}x{height} pixels, but its boxes say {header.Width}x{header.Height}, shown as {header.DisplayWidth}x{header.DisplayHeight}."));
            }

            context.CancellationToken.ThrowIfCancellationRequested();

            // Shrink-on-load: scale the decoded frame down by 2, 4 or 8 (Fant averages every pixel), so only the small
            // picture is copied out. Only when WIC has applied the transforms, so the size is the one shown.
            void* source = frame;
            (int frameWidth, int frameHeight) = ((int)width, (int)height);
            bool scaled = false;
            bool shown = width == (uint)header.DisplayWidth && height == (uint)header.DisplayHeight;
            if (scaledTo is (int scaledWidth, int scaledHeight) && shown)
            {
                Check(Call(factory, 11, &scaler), "creating a scaler", header.Format); // IWICImagingFactory::CreateBitmapScaler
                Check(((delegate* unmanaged[Stdcall]<void*, void*, uint, uint, int, int>)Vtable(scaler)[8])(scaler, frame, (uint)scaledWidth, (uint)scaledHeight, InterpolationFant), "scaling the image", header.Format); // IWICBitmapScaler::Initialize
                source = scaler;
                (width, height) = ((uint)scaledWidth, (uint)scaledHeight);
                scaled = true;
            }

            Check(Call(factory, 10, &converter), "creating a pixel converter", header.Format); // IWICImagingFactory::CreateFormatConverter
            Guid wanted = wicFormat;
            Check(((delegate* unmanaged[Stdcall]<void*, void*, Guid*, int, void*, double, int, int>)Vtable(converter)[8])(converter, source, &wanted, DitherNone, null, 0, PaletteCustom), "converting the pixels", header.Format); // IWICFormatConverter::Initialize

            image = new Image((int)width, (int)height, target);
            int stride = image.Width * (target == PixelFormat.Rgba64 ? 8 : 4);
            Span<byte> pixels = image.GetPixelBytes();
            fixed (byte* destination = pixels)
            {
                // IWICBitmapSource::CopyPixels: the whole image, decoded here.
                Check(((delegate* unmanaged[Stdcall]<void*, void*, uint, uint, byte*, int>)Vtable(converter)[7])(converter, null, (uint)stride, (uint)pixels.Length, destination), "decoding the pixels", header.Format);
            }

            if (header.HasAlpha)
            {
                // The HEIF decoder gives the colour and the alpha plane separately: the frame's pixels are opaque.
                AddAlpha(frame, image, header.Format, frameWidth, frameHeight);
            }

            image.ColorSpace = header.ColorSpace ?? ColorSpace.Srgb;
            Image result = image;
            image = null;
            return scaled ? result : AsShown(result, header, systemApplied: true);
        }
        finally
        {
            image?.Dispose();
            Release(converter);
            Release(scaler);
            Release(frame);
            Release(decoder);
            Release(stream);
            Release(factory);
        }
    }

    // IWICBitmapSourceTransform::CopyPixels with GUID_WICPixelFormat8bppAlpha gives the alpha plane, the same way up as
    // the colour; it goes into the image's alpha channel unchanged (the colour isn't premultiplied).
    private static void AddAlpha(void* frame, Image image, ImageFormat format, int frameWidth, int frameHeight)
    {
        void* transform = null;
        try
        {
            Guid interfaceId = SourceTransformInterface;
            Check(((delegate* unmanaged[Stdcall]<void*, Guid*, void**, int>)Vtable(frame)[0])(frame, &interfaceId, &transform), "reading the alpha plane", format); // IUnknown::QueryInterface
            // The plane at the frame's size; averaged down below when the colour was scaled.
            int width = frameWidth;
            int height = frameHeight;
            byte[] alpha = new byte[width * height];
            Guid alphaFormat = Alpha8;
            int result;
            fixed (byte* plane = alpha)
            {
                result = ((delegate* unmanaged[Stdcall]<void*, void*, uint, uint, Guid*, int, uint, uint, byte*, int>)Vtable(transform)[3])(
                    transform, null, (uint)width, (uint)height, &alphaFormat, 0, (uint)width, (uint)alpha.Length, plane);
            }

            if (result == UnsupportedPixelFormat)
            {
                // Better no image than one that has silently lost its transparency.
                throw new UnsupportedImageFormatException(
                    "Windows can't read this " + format.Name + " file's alpha plane, so it can't be decoded without losing its transparency.",
                    format);
            }

            Check(result, "reading the alpha plane", format);
            if (width != image.Width || height != image.Height)
            {
                alpha = Shrink(alpha, width, height, image.Width, image.Height);
            }

            Span<byte> pixels = image.GetPixelBytes();
            if (image.PixelFormat == PixelFormat.Rgba64)
            {
                for (int i = 0; i < alpha.Length; i++)
                {
                    ushort value = (ushort)(alpha[i] * 257);
                    pixels[(i * 8) + 6] = (byte)value;
                    pixels[(i * 8) + 7] = (byte)(value >> 8);
                }
            }
            else
            {
                for (int i = 0; i < alpha.Length; i++)
                {
                    pixels[(i * 4) + 3] = alpha[i];
                }
            }
        }
        finally
        {
            Release(transform);
        }
    }

    // Averages a plane down to a smaller size: each output pixel is the mean of the source pixels it covers.
    private static byte[] Shrink(byte[] plane, int width, int height, int toWidth, int toHeight)
    {
        byte[] result = new byte[toWidth * toHeight];
        for (int y = 0; y < toHeight; y++)
        {
            int top = (int)((long)y * height / toHeight);
            int bottom = Math.Max(top + 1, (int)((long)(y + 1) * height / toHeight));
            for (int x = 0; x < toWidth; x++)
            {
                int left = (int)((long)x * width / toWidth);
                int right = Math.Max(left + 1, (int)((long)(x + 1) * width / toWidth));
                int sum = 0;
                for (int sy = top; sy < bottom; sy++)
                {
                    for (int sx = left; sx < right; sx++)
                    {
                        sum += plane[(sy * width) + sx];
                    }
                }

                int count = (bottom - top) * (right - left);
                result[(y * toWidth) + x] = (byte)((sum + (count / 2)) / count);
            }
        }

        return result;
    }

    private static bool FindHeifExtension()
    {
        try
        {
            using var com = new ComScope();
            void* decoder = null;
            int result = CreateHeifDecoder(&decoder);
            Release(decoder);
            return result == SOk;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    private static int CreateHeifDecoder(void** decoder) => CreateInstance(HeifDecoder, BitmapDecoderInterface, decoder);

    private static int CreateInstance(Guid classId, Guid interfaceId, void** instance) =>
        NativeMethods.CoCreateInstance(&classId, null, ClassContextInProcessServer, &interfaceId, instance);

    private static void** Vtable(void* instance) => *(void***)instance;

    private static int Call(void* instance, int slot, void** result) =>
        ((delegate* unmanaged[Stdcall]<void*, void**, int>)Vtable(instance)[slot])(instance, result);

    private static void Release(void* instance)
    {
        if (instance is not null)
        {
            ((delegate* unmanaged[Stdcall]<void*, uint>)Vtable(instance)[2])(instance);
        }
    }

    private static void Check(int result, string what, ImageFormat format)
    {
        if (result >= 0)
        {
            return;
        }

        throw result switch
        {
            BadImage or BadHeader or InvalidFileFormat => new ImageFormatException(string.Create(
                CultureInfo.InvariantCulture,
                $"The {format.Name} file is damaged: Windows reported 0x{result:X8} while {what}.")),
            ClassNotRegistered or ComponentNotFound or UnsupportedOperation or CodecNotFound => new UnsupportedImageFormatException(Missing(format), format),
            _ => new ImageCodecException(
                string.Create(CultureInfo.InvariantCulture, $"Windows reported 0x{result:X8} while {what} ({format.Name})."),
                "Windows Imaging Component",
                Marshal.GetExceptionForHR(result)!),
        };
    }

    private static string Missing(ImageFormat format) => format == ImageFormat.Avif
        ? "AVIF decoding on Windows needs the HEIF Image Extension and the AV1 Video Extension from the Microsoft Store."
        : "HEIC decoding on Windows needs the HEIF Image Extension and HEVC Video Extensions from the Microsoft Store.";

    /// <summary>Enters COM for this thread if it hasn't, and leaves it again at the end.</summary>
    private readonly struct ComScope : IDisposable
    {
        private readonly bool _entered;

        public ComScope()
        {
            int result = NativeMethods.CoInitializeEx(null, ComMultithreaded);
            _entered = result is SOk or SFalse;
            if (!_entered && result != ChangedMode)
            {
                Marshal.ThrowExceptionForHR(result);
            }
        }

        public void Dispose()
        {
            if (_entered)
            {
                NativeMethods.CoUninitialize();
            }
        }
    }
}
