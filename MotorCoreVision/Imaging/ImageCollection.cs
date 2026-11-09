using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using DSProcessingTool.ViewModel;
using OpenCvSharp;

namespace FAVIS
{
    public sealed class ImageCollection : IDisposable, IList<ImageFormat>
    {
        private static readonly Size ThumbnailSize = new Size(320, 240);
        private SortedDictionary<int, ImageFormat> _images =
            new SortedDictionary<int, ImageFormat>();
        private SortedDictionary<int, ImageFormat> _thumbnails =
            new SortedDictionary<int, ImageFormat>();
        private Dictionary<int, string> _imageNames =
            new Dictionary<int, string>();

        public ImageCollection()
        {
        }

        public ImageCollection(IEnumerable<ImageFormat> processImages)
        {
            if (processImages == null)
            {
                throw new ArgumentNullException(nameof(processImages));
            }

            try
            {
                int imageIndex = 0;
                foreach (ImageFormat processImage in processImages)
                {
                    imageIndex++;
                    if (processImage == null ||
                        processImage.Width <= 0 ||
                        processImage.Height <= 0 ||
                        processImage.array == null)
                    {
                        throw new InvalidOperationException(
                            $"Process 결과 이미지 {imageIndex}의 크기 또는 데이터가 올바르지 않습니다.");
                    }

                    long requiredLength =
                        ImageCollectionExtensions.GetRequiredBufferLength(processImage);
                    if (requiredLength <= 0 ||
                        processImage.array.LongLength < requiredLength)
                    {
                        throw new InvalidOperationException(
                            $"Process 결과 이미지 {imageIndex}의 버퍼가 이미지 크기보다 작습니다.");
                    }

                    string imageName = processImage.ResultName;
                    if (string.IsNullOrWhiteSpace(imageName))
                    {
                        imageName = processImage.Name;
                    }

                    if (string.IsNullOrWhiteSpace(imageName))
                    {
                        imageName = "Image_" + imageIndex;
                    }

                    Add(imageIndex, processImage, imageName);
                }

                if (Count == 0)
                {
                    throw new InvalidOperationException("변환할 Process 결과 이미지가 없습니다.");
                }
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public IEnumerable<int> Indexes
        {
            get { return _images.Keys; }
        }

        public int Count
        {
            get { return _images.Count; }
        }

        public ImageFormat this[int index]
        {
            get { return _images[GetKeyAt(index)]; }
            set { Add(GetKeyAt(index), value); }
        }

        public bool IsReadOnly
        {
            get { return false; }
        }

        public void Add(ImageFormat image)
        {
            Add(GetNextKey(), image);
        }

        public void Clear()
        {
            foreach (ImageFormat image in _images.Values)
            {
                image.Dispose();
            }

            _images.Clear();
            foreach (ImageFormat thumbnail in _thumbnails.Values)
            {
                thumbnail.Dispose();
            }

            _thumbnails.Clear();
            _imageNames.Clear();
        }

        public bool Contains(ImageFormat image)
        {
            return IndexOf(image) >= 0;
        }

        public void CopyTo(ImageFormat[] array, int arrayIndex)
        {
            if (array == null)
            {
                throw new ArgumentNullException(nameof(array));
            }

            if (arrayIndex < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(arrayIndex));
            }

            if (arrayIndex > array.Length || array.Length - arrayIndex < Count)
            {
                throw new ArgumentException("The destination array is too small.", nameof(array));
            }

            foreach (ImageFormat image in _images.Values)
            {
                array[arrayIndex++] = image;
            }
        }

        public int IndexOf(ImageFormat image)
        {
            int index = 0;
            foreach (ImageFormat candidate in _images.Values)
            {
                if (EqualityComparer<ImageFormat>.Default.Equals(candidate, image))
                {
                    return index;
                }

                index++;
            }

            return -1;
        }

        public void Insert(int index, ImageFormat image)
        {
            if (index < 0 || index > Count)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }

            ValidateImage(image);
            if (index == Count)
            {
                Add(image);
                return;
            }

            ImageFormat insertedImage = new ImageFormat(image, true);
            ImageFormat insertedThumbnail;
            try
            {
                insertedThumbnail = CreateThumbnail(insertedImage);
            }
            catch
            {
                insertedImage.Dispose();
                throw;
            }

            var newImages = new SortedDictionary<int, ImageFormat>();
            var newThumbnails = new SortedDictionary<int, ImageFormat>();
            var newImageNames = new Dictionary<int, string>();
            int newKey = 1;
            bool inserted = false;
            bool committed = false;
            try
            {
                foreach (KeyValuePair<int, ImageFormat> entry in _images)
                {
                    if (!inserted && newKey - 1 == index)
                    {
                        newImages.Add(newKey, insertedImage);
                        newThumbnails.Add(newKey, insertedThumbnail);
                        newKey++;
                        inserted = true;
                    }

                    newImages.Add(newKey, entry.Value);
                    newThumbnails.Add(newKey, _thumbnails[entry.Key]);
                    string imageName;
                    if (_imageNames.TryGetValue(entry.Key, out imageName))
                    {
                        newImageNames.Add(newKey, imageName);
                    }

                    newKey++;
                }

                _images = newImages;
                _thumbnails = newThumbnails;
                _imageNames = newImageNames;
                committed = true;
            }
            catch
            {
                if (!committed)
                {
                    insertedImage.Dispose();
                    insertedThumbnail.Dispose();
                }

                throw;
            }
        }

        public bool Remove(ImageFormat image)
        {
            int index = IndexOf(image);
            if (index < 0)
            {
                return false;
            }

            RemoveAt(index);
            return true;
        }

        public void RemoveAt(int index)
        {
            int key = GetKeyAt(index);
            _images[key].Dispose();
            _images.Remove(key);

            _thumbnails[key].Dispose();
            _thumbnails.Remove(key);
            _imageNames.Remove(key);
        }

        public void Add(int index, Mat image)
        {
            Add(index, image, null);
        }

        public void Add(int index, Mat image, string imageName)
        {
            ValidateIndex(index);
            if (image == null || image.Empty())
            {
                throw new ArgumentException("Image is empty.", nameof(image));
            }

            ImageFormat processImage = CreateImageFormat(image, imageName);
            AddOwnedImage(index, processImage, imageName);
        }

        public void Add(int index, ImageFormat image)
        {
            Add(index, image, null);
        }

        public void Add(int index, ImageFormat image, string imageName)
        {
            ValidateIndex(index);
            ValidateImage(image);

            ImageFormat processImage = new ImageFormat(image, true);
            AddOwnedImage(index, processImage, imageName);
        }

        public bool TryGetImage(int index, out ImageFormat image)
        {
            return _images.TryGetValue(index, out image);
        }

        public bool TryGetThumbnail(int index, out ImageFormat image)
        {
            return _thumbnails.TryGetValue(index, out image);
        }

        public bool TryGetImageName(int index, out string imageName)
        {
            return _imageNames.TryGetValue(index, out imageName);
        }

        public ImageCollection Clone()
        {
            var clone = new ImageCollection();
            try
            {
                foreach (KeyValuePair<int, ImageFormat> entry in _images)
                {
                    string imageName;
                    clone.Add(
                        entry.Key,
                        entry.Value,
                        _imageNames.TryGetValue(entry.Key, out imageName) ? imageName : null);
                }

                return clone;
            }
            catch
            {
                clone.Dispose();
                throw;
            }
        }

        public ImageFormat GetFirstImage()
        {
            return _images.Count == 0 ? null : _images.First().Value;
        }

        public IEnumerator<ImageFormat> GetEnumerator()
        {
            return _images.Values.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        public void Dispose()
        {
            Clear();
        }

        private int GetKeyAt(int index)
        {
            if (index < 0 || index >= Count)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }

            return _images.Keys.ElementAt(index);
        }

        private int GetNextKey()
        {
            int lastKey = _images.Count == 0 ? 0 : _images.Keys.Last();
            if (lastKey == int.MaxValue)
            {
                throw new InvalidOperationException("No image index is available.");
            }

            return lastKey + 1;
        }

        private void AddOwnedImage(int index, ImageFormat image, string imageName)
        {
            ImageFormat thumbnail;
            try
            {
                thumbnail = CreateThumbnail(image);
            }
            catch
            {
                image.Dispose();
                throw;
            }

            ImageFormat previousImage;
            if (_images.TryGetValue(index, out previousImage))
            {
                previousImage.Dispose();
            }

            ImageFormat previousThumbnail;
            if (_thumbnails.TryGetValue(index, out previousThumbnail))
            {
                previousThumbnail.Dispose();
            }

            _images[index] = image;
            _thumbnails[index] = thumbnail;
            if (string.IsNullOrWhiteSpace(imageName))
            {
                _imageNames.Remove(index);
            }
            else
            {
                _imageNames[index] = imageName;
            }
        }

        private static void ValidateIndex(int index)
        {
            if (index <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(index),
                    "Image index must be greater than zero.");
            }
        }

        private static void ValidateImage(ImageFormat image)
        {
            if (image == null ||
                image.Width <= 0 ||
                image.Height <= 0 ||
                image.array == null)
            {
                throw new ArgumentException("Image is empty.", nameof(image));
            }

            long requiredLength = ImageCollectionExtensions.GetRequiredBufferLength(image);
            if (requiredLength <= 0 || image.array.LongLength < requiredLength)
            {
                throw new ArgumentException(
                    "Image buffer is smaller than its dimensions and pixel format.",
                    nameof(image));
            }
        }

        private static ImageFormat CreateImageFormat(Mat image, string imageName)
        {
            Mat continuousImage = image.IsContinuous() ? null : image.Clone();
            Mat sourceImage = continuousImage ?? image;
            try
            {
                string name = string.IsNullOrWhiteSpace(imageName)
                    ? null
                    : imageName;
                var processImage = new ImageFormat(
                    sourceImage.Width,
                    sourceImage.Height,
                    sourceImage.Type(),
                    name);
                try
                {
                    ValidateImage(processImage);
                    long requiredLength =
                        ImageCollectionExtensions.GetRequiredBufferLength(processImage);
                    MarshalCopy(sourceImage, processImage, requiredLength);
                    processImage.Path = name;
                    processImage.ResultName = name;
                    return processImage;
                }
                catch
                {
                    processImage.Dispose();
                    throw;
                }
            }
            finally
            {
                continuousImage?.Dispose();
            }
        }

        private static void MarshalCopy(Mat source, ImageFormat destination, long length)
        {
            if (length > int.MaxValue || destination.array.Length < length)
            {
                throw new InvalidOperationException(
                    "Image buffer exceeds the supported size.");
            }

            System.Runtime.InteropServices.Marshal.Copy(
                source.Data,
                destination.array,
                0,
                (int)length);
        }

        private static ImageFormat CreateThumbnail(ImageFormat image)
        {
            string imageName = string.IsNullOrWhiteSpace(image.ResultName)
                ? image.Name
                : image.ResultName;
            double scale = Math.Min(
                (double)ThumbnailSize.Width / image.Width,
                (double)ThumbnailSize.Height / image.Height);

            if (scale >= 1.0)
            {
                return CopyPixels(image, imageName);
            }

            using (Mat sourceImage = image.ToMat())
            using (var thumbnail = new Mat())
            {
                int width = Math.Max(1, (int)Math.Round(image.Width * scale));
                int height = Math.Max(1, (int)Math.Round(image.Height * scale));
                Cv2.Resize(
                    sourceImage,
                    thumbnail,
                    new Size(width, height),
                    0,
                    0,
                    InterpolationFlags.Area);
                return CreateImageFormat(thumbnail, imageName);
            }
        }

        private static ImageFormat CopyPixels(ImageFormat source, string imageName)
        {
            var copy = new ImageFormat(
                source.Width,
                source.Height,
                source.PixelFormat,
                imageName);
            try
            {
                long requiredLength = ImageCollectionExtensions.GetRequiredBufferLength(source);
                if (requiredLength > int.MaxValue ||
                    source.array.LongLength < requiredLength ||
                    copy.array == null ||
                    copy.array.LongLength < requiredLength)
                {
                    throw new InvalidOperationException(
                        "Image buffer is smaller than its dimensions and pixel format.");
                }

                Array.Copy(source.array, copy.array, (int)requiredLength);
                copy.Path = source.Path;
                copy.ResultName = source.ResultName;
                return copy;
            }
            catch
            {
                copy.Dispose();
                throw;
            }
        }
    }
}
