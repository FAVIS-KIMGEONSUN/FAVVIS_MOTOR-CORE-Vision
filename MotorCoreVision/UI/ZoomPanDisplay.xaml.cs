using System;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DSProcessingTool.ViewModel;
using OpenCvSharp;
using OpenCvSharp.WpfExtensions;
using WpfPoint = System.Windows.Point;
using WpfVector = System.Windows.Vector;

namespace FAVIS
{
    public partial class ZoomPanDisplay : UserControl
    {
        public static readonly DependencyProperty SourceProperty =
            DependencyProperty.Register(
                nameof(Source),
                typeof(ImageCollection),
                typeof(ZoomPanDisplay),
                new PropertyMetadata(null, OnSourceChanged));

        public static readonly DependencyProperty ShowImageSizeProperty =
            DependencyProperty.Register(
                nameof(ShowImageSize),
                typeof(bool),
                typeof(ZoomPanDisplay),
                new PropertyMetadata(false, OnShowImageSizeChanged));

        private const double MinZoom = 1.0;
        private const double MaxZoom = 12.0;
        private const double ZoomStep = 1.15;

        private WpfPoint _lastMousePosition;
        private bool _isPanning;
        private ImageCollection _source;
        private readonly ObservableCollection<ThumbnailItem> _thumbnails =
            new ObservableCollection<ThumbnailItem>();

        public ZoomPanDisplay()
        {
            InitializeComponent();
            ThumbnailListBox.ItemsSource = _thumbnails;
        }

        public ImageCollection Source
        {
            get { return (ImageCollection)GetValue(SourceProperty); }
            set { SetValue(SourceProperty, value); }
        }

        public bool ShowImageSize
        {
            get { return (bool)GetValue(ShowImageSizeProperty); }
            set { SetValue(ShowImageSizeProperty, value); }
        }

        private static void OnSourceChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
        {
            var display = (ZoomPanDisplay)dependencyObject;
            ImageCollection previous = e.OldValue as ImageCollection;
            display.LoadImageCollection(e.NewValue as ImageCollection);
            if (previous != null && !ReferenceEquals(previous, e.NewValue))
            {
                previous.Dispose();
            }
        }

        private static void OnShowImageSizeChanged(
            DependencyObject dependencyObject,
            DependencyPropertyChangedEventArgs e)
        {
            var display = (ZoomPanDisplay)dependencyObject;
            if (display._source != null)
            {
                display.LoadImageCollection(display._source);
            }
        }

        private void LoadImageCollection(ImageCollection imageCollection)
        {
            _source = imageCollection;
            _thumbnails.Clear();
            DisplayImage.Source = null;
            ResetView();

            if (imageCollection == null)
            {
                return;
            }

            foreach (int index in imageCollection.Indexes)
            {
                ImageFormat image;
                if (imageCollection.TryGetImage(index, out image))
                {
                    ImageFormat thumbnailImage;
                    if (!imageCollection.TryGetThumbnail(index, out thumbnailImage))
                    {
                        continue;
                    }

                    string imageName;
                    string label = imageCollection.TryGetImageName(index, out imageName) &&
                                   !string.IsNullOrWhiteSpace(imageName)
                        ? imageName
                        : $"Image {index}";
                    if (ShowImageSize)
                    {
                        label += Environment.NewLine + $"{image.Width}x{image.Height}";
                    }

                    AddThumbnail(label, index, thumbnailImage);
                }
            }

            if (_thumbnails.Count > 0)
            {
                ThumbnailListBox.SelectedIndex = 0;
                DisplaySelectedImage(_thumbnails[0].Index);
            }
        }

        private void AddThumbnail(string label, int index, ImageFormat thumbnailImage)
        {
            BitmapSource thumbnailBitmap = CreateBitmapSource(thumbnailImage);
            var thumbnail = new ThumbnailItem(label, index, thumbnailBitmap);
            _thumbnails.Add(thumbnail);
        }

        private void OnThumbnailSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var thumbnail = ThumbnailListBox.SelectedItem as ThumbnailItem;
            if (thumbnail == null)
            {
                return;
            }

            DisplaySelectedImage(thumbnail.Index);
            ResetView();
        }

        private void DisplaySelectedImage(int index)
        {
            if (_source == null)
            {
                DisplayImage.Source = null;
                return;
            }

            ImageFormat image;
            if (!_source.TryGetImage(index, out image))
            {
                DisplayImage.Source = null;
                return;
            }

            DisplayImage.Source = CreateBitmapSource(image);
        }

        private static BitmapSource CreateBitmapSource(ImageFormat image)
        {
            using (Mat mat = image.ToMat())
            {
                BitmapSource bitmap = mat.ToBitmapSource();
                bitmap.Freeze();
                return bitmap;
            }
        }

        private void OnMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (IsThumbnailInteraction(e.OriginalSource))
            {
                return;
            }

            if (DisplayImage.Source == null)
            {
                return;
            }

            var oldZoom = ImageScale.ScaleX;
            var newZoom = e.Delta > 0
                ? Math.Min(MaxZoom, oldZoom * ZoomStep)
                : Math.Max(MinZoom, oldZoom / ZoomStep);

            if (Math.Abs(newZoom - oldZoom) < 0.001)
            {
                return;
            }

            var pointer = e.GetPosition(this);
            var center = new WpfPoint(ActualWidth / 2.0, ActualHeight / 2.0);
            var imagePoint = new WpfVector(pointer.X - center.X - ImageTranslation.X,
                                           pointer.Y - center.Y - ImageTranslation.Y);
            var zoomRatio = newZoom / oldZoom;

            ImageTranslation.X -= imagePoint.X * (zoomRatio - 1.0);
            ImageTranslation.Y -= imagePoint.Y * (zoomRatio - 1.0);
            ImageScale.ScaleX = newZoom;
            ImageScale.ScaleY = newZoom;

            e.Handled = true;
        }

        private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (IsThumbnailInteraction(e.OriginalSource))
            {
                return;
            }

            if (DisplayImage.Source == null)
            {
                return;
            }

            Focus();
            _isPanning = true;
            _lastMousePosition = e.GetPosition(this);
            Mouse.Capture(this, CaptureMode.SubTree);
            Cursor = Cursors.Hand;
            e.Handled = true;
        }

        private void OnMouseMove(object sender, MouseEventArgs e)
        {
            if (IsThumbnailInteraction(e.OriginalSource))
            {
                return;
            }

            if (!_isPanning)
            {
                return;
            }

            var currentPosition = e.GetPosition(this);
            var delta = currentPosition - _lastMousePosition;
            ImageTranslation.X += delta.X;
            ImageTranslation.Y += delta.Y;
            _lastMousePosition = currentPosition;
            e.Handled = true;
        }

        private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (IsThumbnailInteraction(e.OriginalSource))
            {
                return;
            }

            EndPanning();
            e.Handled = true;
        }

        private void OnLostMouseCapture(object sender, MouseEventArgs e)
        {
            EndPanning();
        }

        private void OnMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (IsThumbnailInteraction(e.OriginalSource))
            {
                return;
            }

            ResetView();
            e.Handled = true;
        }

        private bool IsThumbnailInteraction(object originalSource)
        {
            var source = originalSource as DependencyObject;
            var visual = source as Visual;
            return source == ThumbnailListBox ||
                   (visual != null && visual.IsDescendantOf(ThumbnailListBox));
        }

        private void EndPanning()
        {
            if (!_isPanning)
            {
                return;
            }

            _isPanning = false;
            if (Mouse.Captured == this)
            {
                Mouse.Capture(null);
            }
            Cursor = Cursors.Arrow;
        }

        private void ResetView()
        {
            ImageScale.ScaleX = 1.0;
            ImageScale.ScaleY = 1.0;
            ImageTranslation.X = 0.0;
            ImageTranslation.Y = 0.0;
        }

        private sealed class ThumbnailItem
        {
            public ThumbnailItem(string label, int index, BitmapSource image)
            {
                Label = label;
                Index = index;
                Image = image;
            }

            public string Label { get; private set; }
            public int Index { get; private set; }
            public BitmapSource Image { get; private set; }
        }
    }
}
