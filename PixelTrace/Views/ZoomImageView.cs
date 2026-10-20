using Android.Content;
using Android.Graphics;
using Android.Views;
using Android.Widget;

namespace PixelTrace.Mobile;

internal sealed class ZoomImageView : ImageView
{
    private Matrix Transform { get; } = new();
    private ScaleListener PinchListener { get; }
    private TouchListener DragListener { get; }
    private ScaleGestureDetector Pinch { get; }
    private GestureDetector Gestures { get; }
    private float fitScale, zoom = 1, offsetX, offsetY;
    private int imageWidth, imageHeight;

    public ZoomImageView(Context context) : base(context)
    {
        SetScaleType(ScaleType.Matrix);
        PinchListener = new(this);
        DragListener = new(this);
        Pinch = new(context, PinchListener);
        Gestures = new(context, DragListener);
        Gestures.SetOnDoubleTapListener(DragListener);
        Clickable = true;
        Focusable = true;
    }

    public void SetPicture(Bitmap bitmap)
    {
        imageWidth = bitmap.Width;
        imageHeight = bitmap.Height;
        SetImageBitmap(bitmap);
        ResetZoom();
    }

    public void ResetZoom()
    {
        if (imageWidth == 0 || imageHeight == 0 || Width == 0 || Height == 0)
        {
            return;
        }

        fitScale = Math.Min((float)Width / imageWidth, (float)Height / imageHeight);
        zoom = 1;
        offsetX = (Width - imageWidth * fitScale) / 2;
        offsetY = (Height - imageHeight * fitScale) / 2;
        ApplyTransform();
    }

    protected override void OnSizeChanged(int width, int height, int oldWidth, int oldHeight)
    {
        base.OnSizeChanged(width, height, oldWidth, oldHeight);
        ResetZoom();
    }

    public override bool OnTouchEvent(MotionEvent? e)
    {
        if (e is null)
        {
            return false;
        }

        Parent?.RequestDisallowInterceptTouchEvent(true);
        Pinch.OnTouchEvent(e);
        Gestures.OnTouchEvent(e);
        if (e.ActionMasked is MotionEventActions.Up or MotionEventActions.Cancel)
        {
            Parent?.RequestDisallowInterceptTouchEvent(false);
        }

        return true;
    }

    private void ScaleAt(float factor, float focusX, float focusY)
    {
        if (fitScale <= 0)
        {
            return;
        }

        float next = Math.Clamp(zoom * factor, 1, 8);
        float ratio = next / zoom;
        offsetX = focusX - (focusX - offsetX) * ratio;
        offsetY = focusY - (focusY - offsetY) * ratio;
        zoom = next;
        ApplyTransform();
    }

    private void ApplyTransform()
    {
        float scale = fitScale * zoom;
        float width = imageWidth * scale, height = imageHeight * scale;
        offsetX = width <= Width ? (Width - width) / 2 : Math.Clamp(offsetX, Width - width, 0);
        offsetY = height <= Height ? (Height - height) / 2 : Math.Clamp(offsetY, Height - height, 0);
        Transform.SetScale(scale, scale);
        Transform.PostTranslate(offsetX, offsetY);
        ImageMatrix = Transform;
    }

    private sealed class ScaleListener(ZoomImageView owner) : ScaleGestureDetector.SimpleOnScaleGestureListener
    {
        private ZoomImageView Owner { get; } = owner;
        public override bool OnScale(ScaleGestureDetector detector)
        {
            Owner.ScaleAt(detector.ScaleFactor, detector.FocusX, detector.FocusY);
            return true;
        }
    }

    private sealed class TouchListener(ZoomImageView owner) : GestureDetector.SimpleOnGestureListener
    {
        private ZoomImageView Owner { get; } = owner;
        public override bool OnDown(MotionEvent e)
            => true;
        public override bool OnSingleTapConfirmed(MotionEvent e)
            => Owner.PerformClick();
        public override bool OnDoubleTap(MotionEvent e)
        {
            if (1 < Owner.zoom)
            {
                Owner.ResetZoom();
            }
            else
            {
                Owner.ScaleAt(3, e.GetX(), e.GetY());
            }

            return true;
        }

        public override bool OnScroll(MotionEvent? first, MotionEvent current, float distanceX, float distanceY)
        {
            if (!Owner.Pinch.IsInProgress)
            {
                Owner.offsetX -= distanceX;
                Owner.offsetY -= distanceY;
                Owner.ApplyTransform();
            }

            return true;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Gestures.Dispose();
            Pinch.Dispose();
            DragListener.Dispose();
            PinchListener.Dispose();
            Transform.Dispose();
        }

        base.Dispose(disposing);
    }
}
