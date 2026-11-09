using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using DeeDiimSensors.DSProcessingTool;
using DSProcessingTool.ViewModel;
using OpenCvSharp;
using OpenCvSharp.Extensions;

namespace FAVIS
{
    public static class ImageCollectionExtensions
    {
        /// <summary>
        /// IEnumerable<ImageFormat> 컬렉션을 복사하여 새로운 List<ImageFormat>를 반환합니다.
        /// </summary>
        /// <param name="images">복사할 ImageFormat 컬렉션</param>
        /// <returns>복사된 ImageFormat의 새로운 List</returns>
        /// <exception cref="InvalidOperationException">복사할 Process 이미지가 비어 있을 때 발생합니다.</exception>
        public static List<ImageFormat> Clone(this IEnumerable<ImageFormat> images)
        {
            var list = new List<ImageFormat>();
            foreach (ImageFormat image in images)
            {
                if (image == null || image.array == null || image.array.Length == 0)
                {
                    throw new InvalidOperationException("복사할 Process 이미지가 비어 있습니다.");
                }

                list.Add(new ImageFormat(image, true));
            }

            return list;
        }

        /// <summary>
        /// DISPLAY용 이미지가 아닌 Process 이미지들만 복사하여 새로운 List<ImageFormat>를 반환합니다.
        /// </summary>
        /// <param name="images">복사할 이미지 리스트</param>
        /// <returns>DISPLAY용 이미지가 아닌 Process 이미지들의 새로운 List</returns>
        /// <exception cref="InvalidOperationException">복사할 Process 이미지가 비어 있을 때 발생합니다.</exception>
        public static List<ImageFormat> CopyWithoutDisplayImage(this IEnumerable<ImageFormat> images)
        {
            var list = new List<ImageFormat>();

            int imageIndex = 0;
            foreach (ImageFormat image in images)
            {
                imageIndex++;
                if (image == null || image.array == null || image.array.Length == 0)
                {
                    throw new InvalidOperationException("복사할 Process 이미지가 비어 있습니다.");
                }

                string imageName = image.ResultName;
                if (string.IsNullOrWhiteSpace(imageName))
                {
                    imageName = image.Name;
                }
                if (string.IsNullOrWhiteSpace(imageName))
                {
                    imageName = "Image_" + imageIndex;
                }

                if (imageName.StartsWith("DISPLAY", StringComparison.OrdinalIgnoreCase))
                {
                    continue; // Skip images with names starting with "DISPLAY"
                }

                list.Add(new ImageFormat(image, false));
            }
            return list;
        }

        public static ImageCollection ToImageCollection(this IEnumerable<ImageFormat> processImages)
        {
            return new ImageCollection(processImages);
        }

        public static Cognex.VisionPro.CogImage24PlanarColor ToCognexImage(this ImageCollection image)
        {
            ImageFormat firstImage = image?.GetFirstImage();
            if (firstImage == null)
            {
                throw new InvalidOperationException(
                    "변환할 이미지가 없습니다. 먼저 이미지를 촬영하거나 불러오세요.");
            }

            if (firstImage.PixelFormat.Depth != 0)
            {
                throw new NotSupportedException(
                    "CogImage24PlanarColor 변환에는 8비트 이미지가 필요합니다.");
            }

            using (Mat source = firstImage.ToMat())
            using (var colorImage = new Mat())
            {
                switch (firstImage.PixelFormat.Channels)
                {
                    case 1:
                        Cv2.CvtColor(source, colorImage, ColorConversionCodes.GRAY2BGR);
                        break;
                    case 3:
                        source.CopyTo(colorImage);
                        break;
                    case 4:
                        Cv2.CvtColor(source, colorImage, ColorConversionCodes.BGRA2BGR);
                        break;
                    default:
                        throw new NotSupportedException(
                            "지원하지 않는 이미지 채널 수입니다: " +
                            firstImage.PixelFormat.Channels);
                }

                using (Bitmap bitmap = BitmapConverter.ToBitmap(colorImage))
                {
                    return new Cognex.VisionPro.CogImage24PlanarColor(bitmap);
                }
            }
        }

        public static Mat ToMat(this ImageFormat processImage)
        {
            if (processImage == null)
            {
                throw new ArgumentNullException(nameof(processImage));
            }

            long requiredLength = GetRequiredBufferLength(processImage);
            if (requiredLength <= 0 ||
                requiredLength > int.MaxValue ||
                processImage.array == null ||
                processImage.array.LongLength < requiredLength)
            {
                throw new InvalidOperationException(
                    "Process 이미지 버퍼가 이미지 크기보다 작거나 올바르지 않습니다.");
            }

            var image = new Mat(
                processImage.Height,
                processImage.Width,
                processImage.PixelFormat);
            try
            {
                if (image.Empty() || !image.IsContinuous())
                {
                    throw new InvalidOperationException(
                        "Process 이미지용 Mat 버퍼를 만들 수 없습니다.");
                }

                Marshal.Copy(
                    processImage.array,
                    0,
                    image.Data,
                    (int)requiredLength);
                return image;
            }
            catch
            {
                image.Dispose();
                throw;
            }
        }

        internal static long GetRequiredBufferLength(ImageFormat image)
        {
            if (image == null || image.Width <= 0 || image.Height <= 0)
            {
                return 0;
            }

            return (long)image.Width *
                image.Height *
                image.PixelFormat.Channels *
                GetBytesPerChannel(image.PixelFormat.Depth);
        }

        internal static int GetBytesPerChannel(int depth)
        {
            switch (depth)
            {
                case 0:
                case 1:
                    return 1;
                case 2:
                case 3:
                case 7:
                    return 2;
                case 4:
                case 5:
                    return 4;
                case 6:
                    return 8;
                default:
                    throw new InvalidOperationException(
                        "지원하지 않는 Process 이미지 픽셀 형식입니다: " + depth);
            }
        }

        public static List<ImageFormat> ToImageFormatList(this ImageCollection images)
        {
            if (images == null)
            {
                throw new ArgumentNullException(nameof(images));
            }

            var processImages = new List<ImageFormat>();
            try
            {
                foreach (int imageIndex in images.Indexes)
                {
                    ImageFormat image;
                    if (!images.TryGetImage(imageIndex, out image))
                    {
                        continue;
                    }

                    if (image == null ||
                        image.Width <= 0 ||
                        image.Height <= 0 ||
                        image.array == null)
                    {
                        throw new InvalidOperationException(
                            $"입력 이미지 인덱스 {imageIndex}의 이미지가 비어 있습니다.");
                    }

                    string imageName;
                    if (!images.TryGetImageName(imageIndex, out imageName) ||
                        string.IsNullOrWhiteSpace(imageName))
                    {
                        imageName = "Image_" + imageIndex;
                    }

                    imageName = Path.GetFileNameWithoutExtension(imageName);
                    if (string.IsNullOrWhiteSpace(imageName))
                    {
                        imageName = "Image_" + imageIndex;
                    }

                    long requiredLength = GetRequiredBufferLength(image);
                    if (requiredLength <= 0 ||
                        requiredLength > int.MaxValue ||
                        image.array.LongLength < requiredLength)
                    {
                        throw new InvalidOperationException(
                            $"입력 이미지 인덱스 {imageIndex}의 버퍼가 이미지 크기보다 작습니다.");
                    }

                    var processImage = new ImageFormat(
                        image.Width,
                        image.Height,
                        image.PixelFormat,
                        imageName);
                    try
                    {
                        if (processImage.array == null ||
                            processImage.array.LongLength < requiredLength)
                        {
                            throw new InvalidOperationException(
                                $"입력 이미지 인덱스 {imageIndex}의 Process 이미지 버퍼를 만들 수 없습니다.");
                        }

                        Array.Copy(image.array, processImage.array, (int)requiredLength);
                        processImage.Path = imageName;
                        processImage.ResultName = imageName;
                        processImages.Add(processImage);
                    }
                    catch
                    {
                        processImage.Dispose();
                        throw;
                    }
                }

                if (processImages.Count == 0)
                {
                    throw new InvalidOperationException("검사에 사용할 입력 이미지가 없습니다.");
                }

                return processImages;
            }
            catch
            {
                foreach (ImageFormat processImage in processImages)
                {
                    processImage.Dispose();
                }

                throw;
            }
        }
    }
}
