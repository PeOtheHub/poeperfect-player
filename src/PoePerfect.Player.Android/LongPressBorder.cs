using Android.Views;
using AView = Android.Views.View;

namespace PoePerfect.Player.Android;

public sealed class LongPressDragEventArgs(double totalX, double totalY, double rawX, double rawY) : EventArgs
{
    public double TotalX { get; } = totalX;

    public double TotalY { get; } = totalY;

    public double RawX { get; } = rawX;

    public double RawY { get; } = rawY;
}

public class LongPressBorder : Border
{
    private readonly NativeLongPressListener _nativeLongPressListener;
    private GestureDetector? _gestureDetector;
    private bool _isLongPressActive;
    private float _lastRawX;
    private float _lastRawY;
    private AView? _platformView;
    private float _pressStartRawX;
    private float _pressStartRawY;

    public event EventHandler<LongPressDragEventArgs>? LongPressed;

    public event EventHandler<LongPressDragEventArgs>? DragMoved;

    public event EventHandler<LongPressDragEventArgs>? DragFinished;

    public LongPressBorder()
    {
        _nativeLongPressListener = new NativeLongPressListener(this);
        HandlerChanged += OnBorderHandlerChanged;
        HandlerChanging += OnBorderHandlerChanging;
    }

    private void OnBorderHandlerChanged(object? sender, EventArgs e)
    {
        AttachNativeTouchHandler();
    }

    private void OnBorderHandlerChanging(object? sender, HandlerChangingEventArgs e)
    {
        DetachNativeTouchHandler();
    }

    private void AttachNativeTouchHandler()
    {
        if (Handler?.PlatformView is not AView platformView)
        {
            return;
        }

        if (ReferenceEquals(_platformView, platformView))
        {
            return;
        }

        DetachNativeTouchHandler();

        _platformView = platformView;
        _platformView.Clickable = true;
        _platformView.LongClickable = true;
        _gestureDetector = new GestureDetector(platformView.Context, _nativeLongPressListener);
        _platformView.Touch += OnPlatformViewTouch;
    }

    private void DetachNativeTouchHandler()
    {
        ResetLongPressState();

        if (_platformView is not null)
        {
            _platformView.Touch -= OnPlatformViewTouch;
        }

        _gestureDetector = null;
        _platformView = null;
    }

    private void OnPlatformViewTouch(object? sender, AView.TouchEventArgs e)
    {
        if (e.Event is null)
        {
            return;
        }

        _gestureDetector?.OnTouchEvent(e.Event);

        switch (e.Event.ActionMasked)
        {
            case MotionEventActions.Down:
                _lastRawX = e.Event.RawX;
                _lastRawY = e.Event.RawY;
                break;

            case MotionEventActions.Move:
                _lastRawX = e.Event.RawX;
                _lastRawY = e.Event.RawY;

                if (_isLongPressActive)
                {
                    DragMoved?.Invoke(
                        this,
                        new LongPressDragEventArgs(_lastRawX - _pressStartRawX, _lastRawY - _pressStartRawY, _lastRawX, _lastRawY));
                    e.Handled = true;
                }

                break;

            case MotionEventActions.Up:
            case MotionEventActions.Cancel:
                _lastRawX = e.Event.RawX;
                _lastRawY = e.Event.RawY;

                if (_isLongPressActive)
                {
                    DragFinished?.Invoke(
                        this,
                        new LongPressDragEventArgs(_lastRawX - _pressStartRawX, _lastRawY - _pressStartRawY, _lastRawX, _lastRawY));
                    ResetLongPressState();
                    e.Handled = true;
                }
                else
                {
                    ResetLongPressState();
                }

                break;
        }
    }

    private void ActivateLongPress()
    {
        if (_platformView is null || _isLongPressActive)
        {
            return;
        }

        _isLongPressActive = true;
        _pressStartRawX = _lastRawX;
        _pressStartRawY = _lastRawY;
        _platformView.Parent?.RequestDisallowInterceptTouchEvent(true);
        _platformView.PerformHapticFeedback(FeedbackConstants.LongPress);
        LongPressed?.Invoke(this, new LongPressDragEventArgs(0, 0, _lastRawX, _lastRawY));
    }

    private void ResetLongPressState()
    {
        if (_platformView is not null)
        {
            _platformView.Parent?.RequestDisallowInterceptTouchEvent(false);
        }

        _isLongPressActive = false;
        _pressStartRawX = 0;
        _pressStartRawY = 0;
    }

    private sealed class NativeLongPressListener(LongPressBorder owner) : GestureDetector.SimpleOnGestureListener
    {
        public override bool OnDown(MotionEvent? e) => true;

        public override void OnLongPress(MotionEvent? e)
        {
            owner.ActivateLongPress();
        }
    }
}
